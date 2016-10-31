﻿// Part of fCraft | Copyright 2009-2015 Matvei Stefarov <me@matvei.org> | BSD-3 | See LICENSE.txt //Copyright (c) 2011-2013 Jon Baker, Glenn Marien and Lao Tszy <Jonty800@gmail.com> //Copyright (c) <2012-2014> <LeChosenOne, DingusBungus> | ProCraft Copyright 2014-2016 Joseph Beauvais <123DMWM@gmail.com>
//#define DEBUG_MOVEMENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using fCraft.AutoRank;
using fCraft.Events;
using fCraft.MapConversion;
using JetBrains.Annotations;
using System.Diagnostics;

namespace fCraft {
    /// <summary> Represents a connection to a Minecraft client. Handles low-level interactions (e.g. networking). </summary>
    public sealed partial class Player {
        public static int SocketTimeout { get; set; }
        public static bool RelayAllUpdates { get; set; }
        const int SleepDelay = 5; // milliseconds
        const int SocketPollInterval = 100; // multiples of SleepDelay, approx. 1 second
        const int PingInterval = 1; // multiples of SocketPollInterval, approx. 3 seconds
        public DateTime LastZoneNotification = DateTime.UtcNow;
        public Stopwatch pingTimer = new Stopwatch();

        static Player() {
            MaxBlockPlacementRange = 32767;
            SocketTimeout = 10000;
        }


        /// <summary> Reason that this player is about to leave / has left the server. Set by Kick. 
        /// This value is undefined until the player is about to disconnect. </summary>
        public LeaveReason LeaveReason { get; private set; }

        /// <summary> Remote IP address of this player. </summary>
        public IPAddress IP { get; private set; }


        bool canReceive = true,
             canSend = true,
             canQueue = true;
        bool unregisterOnKick = true;

        readonly Thread ioThread;
        readonly TcpClient client;
        readonly NetworkStream stream;
        readonly PacketReader reader;
        readonly PacketWriter writer;

        readonly ConcurrentQueue<Packet> priorityOutputQueue = new ConcurrentQueue<Packet>();
        readonly ConcurrentQueue<SetBlockData> blockQueue = new ConcurrentQueue<SetBlockData>();


        internal static Player StartSession( [NotNull] TcpClient tcpClient ) {
            if( tcpClient == null ) throw new ArgumentNullException( "tcpClient" );
            IPAddress ipAddress = ((IPEndPoint)(tcpClient.Client.RemoteEndPoint)).Address;
            IPBanInfo ipBanInfo = IPBanList.Get(ipAddress);
            if (ipBanInfo != null) {
                Logger.Log(LogType.SuspiciousActivity, "Player on banned ip(" + ipAddress.ToString() +") tried to join");
                string bannedMessage = string.Format("IP-banned {0} ago by {1}: {2}",
                     DateTime.UtcNow.Subtract(ipBanInfo.BanDate).ToMiniString(),
                     ipBanInfo.BannedBy, ipBanInfo.BanReason);
                tcpClient.Client.Send(Packet.MakeKick(bannedMessage).Bytes);
                return null;
            }
            return new Player( tcpClient );
        }


        Player( [NotNull] TcpClient tcpClient ) {
            if( tcpClient == null ) throw new ArgumentNullException( "tcpClient" );
            State = SessionState.Connecting;
            LoginTime = DateTime.UtcNow;
            LastActiveTime = DateTime.UtcNow;
            LastPatrolTime = DateTime.UtcNow;
            LeaveReason = LeaveReason.Unknown;
            LastUsedBlockType = Block.None;
            BlocksDeletedThisSession = 0;
            BlocksPlacedThisSession = 0;

            client = tcpClient;
            client.SendTimeout = SocketTimeout;
            client.ReceiveTimeout = SocketTimeout;

            BrushReset();
            Metadata = new MetadataCollection<object>();

            try {
                IP = ( (IPEndPoint)( client.Client.RemoteEndPoint ) ).Address;
                if( Server.RaiseSessionConnectingEvent( IP ) ) return;

                stream = client.GetStream();
                reader = new PacketReader( stream );
                writer = new PacketWriter( stream );

                ioThread = new Thread( IoLoop ) {
                    Name = "ProCraft.Session",
                    CurrentCulture = new CultureInfo( "en-US" )
                };
                ioThread.Start();

            } catch( SocketException ) {
                // Mono throws SocketException when accessing Client.RemoteEndPoint on disconnected sockets
                Disconnect();

            } catch( Exception ex ) {
                Logger.LogAndReportCrash( "Session failed to start", "ProCraft", ex, false );
                Disconnect();
            }
        }


        #region I/O Loop

        void IoLoop() {
            try {
                Server.RaiseSessionConnectedEvent( this );

                // try to log the player in, otherwise die.
                if( !LoginSequence() ) return;
                BandwidthUseMode = Info.BandwidthUseMode;

                // set up some temp variables
                Packet packet = new Packet();
                SetBlockData blockUpdate = new SetBlockData();
                byte[] blockPacket = new byte[8];
                blockPacket[0] = (byte)OpCode.SetBlockServer;
                byte[] bulkBlockPacket = null;
                if (Supports(CpeExt.BulkBlockUpdate)) {
                    bulkBlockPacket = new byte[1282];
                    bulkBlockPacket[0] = (byte)OpCode.BulkBlockUpdate;
                }
                

                int pollCounter = 0, pingCounter = 0;

                // main i/o loop
                while( canSend ) {
                    int packetsSent = 0, blockPacketsSent = 0;

                    // detect player disconnect
                    if( pollCounter > SocketPollInterval ) {
                        if( !client.Connected ||
                            ( client.Client.Poll( 1000, SelectMode.SelectRead ) && client.Client.Available == 0 ) ) {
                            if( Info != null ) {
                                Logger.Log( LogType.Debug,
                                            "Player.IoLoop: Lost connection to player {0} ({1}).", Name, IP );
                            } else {
                                Logger.Log( LogType.Debug,
                                            "Player.IoLoop: Lost connection to unidentified player at {0}.", IP );
                            }
                            LeaveReason = LeaveReason.ClientQuit;
                            return;
                        }
                        if( pingCounter > PingInterval ) {
                            writer.Write( OpCode.Ping );
                            pingTimer.Reset();
                            pingTimer.Start();
                            BytesSent++;
                            pingCounter = 0;
                            MeasureBandwidthUseRates();
                        }
                        pingCounter++;
                        pollCounter = 0;                        
                    }
                    pollCounter++;

                    if( DateTime.UtcNow.Subtract( lastMovementUpdate ) > movementUpdateInterval ) {
                        UpdateVisibleEntities();
                        lastMovementUpdate = DateTime.UtcNow;
                    }

                    // send output to player
                    while (canSend && packetsSent < Server.MaxSessionPacketsPerTick) {
                        if (!priorityOutputQueue.TryDequeue(out packet)) break;

                        if( IsDeaf && packet.OpCode == OpCode.Message ) continue;

                        writer.Write( packet.Bytes );
                        BytesSent += packet.Bytes.Length;
                        packetsSent++;

                        if( packet.OpCode == OpCode.Kick ) {
                            writer.Flush();
                            if( LeaveReason == LeaveReason.Unknown ) LeaveReason = LeaveReason.Kick;
                            return;
                        }

                        if( DateTime.UtcNow.Subtract( lastMovementUpdate ) > movementUpdateInterval ) {
                            UpdateVisibleEntities();
                            lastMovementUpdate = DateTime.UtcNow;
                        }
                    }
                    
                    // send block updates output to player
                    bool useBulk = bulkBlockPacket != null && blockQueue.Length > 160;
                    Map currentMap = WorldMap;
                    while (canSend && blockPacketsSent < Server.MaxBlockPacketsPerTick) {
                        if (!blockQueue.TryDequeue(out blockUpdate)) break;
                        CheckBlock(ref blockUpdate.Block);
                        
                        if (!useBulk) {
                            Packet.ToNetOrder(blockUpdate.X, blockPacket, 1);
                            Packet.ToNetOrder(blockUpdate.Z, blockPacket, 3);
                            Packet.ToNetOrder(blockUpdate.Y, blockPacket, 5);
                            blockPacket[7] = blockUpdate.Block;
                            writer.Write( blockPacket );
                            BytesSent += blockPacket.Length;
                        } else {
                            int index = currentMap.Index(blockUpdate.X, blockUpdate.Y, blockUpdate.Z);
                            Packet.ToNetOrder(index, bulkBlockPacket, 2 + 4 * blockPacketsSent);
                            bulkBlockPacket[2 + 1024 + blockPacketsSent] = blockUpdate.Block;
                        }
                        blockPacketsSent++;
                    }
                    if (canSend && useBulk) {
                        bulkBlockPacket[1] = (byte)(blockPacketsSent - 1);
                        writer.Write( bulkBlockPacket );
                        BytesSent += bulkBlockPacket.Length;
                    }

                    // check if player needs to change worlds
                    if( canSend ) {
                        lock( joinWorldLock ) {
                            if( forcedWorldToJoin != null ) {
                                while( priorityOutputQueue.TryDequeue( out packet ) ) {
                                    writer.Write( packet.Bytes );
                                    BytesSent += packet.Bytes.Length;
                                    packetsSent++;
                                    if( packet.OpCode == OpCode.Kick ) {
                                        writer.Flush();
                                        if( LeaveReason == LeaveReason.Unknown ) LeaveReason = LeaveReason.Kick;
                                        return;
                                    }
                                }
                                if( !JoinWorldNow( forcedWorldToJoin, useWorldSpawn, worldChangeReason ) ) {
                                    Logger.Log( LogType.Warning,
                                                "Player.IoLoop: Player was asked to force-join a world, but it was full." );
                                    KickNow( "World is full.", LeaveReason.ServerFull );
                                }
                                forcedWorldToJoin = null;
                            }
                        }

                        if( DateTime.UtcNow.Subtract( lastMovementUpdate ) > movementUpdateInterval ) {
                            UpdateVisibleEntities();
                            lastMovementUpdate = DateTime.UtcNow;
                        }
                    }


                    // get input from player
                    while( canReceive && stream.DataAvailable ) {
                        byte opcode = reader.ReadByte();
                        switch( (OpCode)opcode ) {

                            case OpCode.Message:
                                if( !ProcessMessagePacket() ) return;
                                break;

                            case OpCode.Teleport:
                                ProcessMovementPacket();
                                break;

                            case OpCode.SetBlockClient:
                                ProcessSetBlockPacket();
                                break;

                            case OpCode.PlayerClick:
                                ProcessPlayerClickPacket();
                                break;

                            case OpCode.Ping:
                                ProcessPingPacket();
                                continue;

                            default:
                                Logger.Log( LogType.SuspiciousActivity,
                                            "Player {0} was kicked after sending an invalid opcode ({1}).",
                                            Name, opcode );
                                KickNow( "Unknown packet opcode " + opcode,
                                         LeaveReason.InvalidOpcodeKick );
                                return;
                        }

                        if( DateTime.UtcNow.Subtract( lastMovementUpdate ) > movementUpdateInterval ) {
                            UpdateVisibleEntities();
                            lastMovementUpdate = DateTime.UtcNow;
                        }
                    }

                    Thread.Sleep( SleepDelay );
                }

            } catch( IOException ) {
                LeaveReason = LeaveReason.ClientQuit;

            } catch( SocketException ) {
                LeaveReason = LeaveReason.ClientQuit;
#if !DEBUG
            } catch( Exception ex ) {
                LeaveReason = LeaveReason.ServerError;
                Logger.LogAndReportCrash( "Error in Player.IoLoop", "ProCraft", ex, false );
#endif
            } finally {
                canQueue = false;
                canSend = false;
                Disconnect();
            }
        }
        #endregion

        public void ProcessPingPacket() {
            pingTimer.Stop();
            int total = 0;
            for( int i = 0; i < 10; i++) {
                if (i == 9) {
                    PingList[i] = (int)pingTimer.ElapsedMilliseconds;
                } else {
                    PingList[i] = PingList[i + 1];
                }
                total += PingList[i];
            }
            if (!IsPlayingCTF) {
                Message((byte)MessageType.BottomRight3, "&SPing: &f{0}&Sms Avg: &f{1}&Sms", PingList[9], PingList.Average());
            }
            BytesReceived++;
        }

        bool ProcessMessagePacket() {
            BytesReceived += 66;
            ResetIdleTimer();
            byte longerMessage = reader.ReadByte();
            string message = reader.ReadString();

            if ( !IsSuper && message.StartsWith( "/womid " ) ) {
                IsUsingWoM = true;
                return true;
            }

            if((message.IndexOf('&') != -1) && (!(Can(Permission.UseColorCodes)))) {
                message = Color.StripColors( message );
            }
            if (longerMessage == 1) {
                message = message + "λ";
            }
#if DEBUG
            ParseMessage( message, false );
#else
            try {
                ParseMessage( message, false );
            } catch( IOException ) {
                throw;
            } catch( SocketException ) {
                throw;
            } catch( Exception ex ) {
                Logger.LogAndReportCrash( "Error while parsing player's message", "ProCraft", ex, false );
                Message( "&WError while handling your message ({0}: {1})." +
                            "It is recommended that you reconnect to the server.",
                            ex.GetType().Name, ex.Message );
            }
#endif
            return true;
        }

        DateTime lastSpamTime = DateTime.MinValue;
        DateTime lastMoveTime = DateTime.MinValue;
        
        void UpdateHeldBlock() {
            byte id = reader.ReadByte();
            if (!Supports(CpeExt.HeldBlock)) {
                HeldBlock = Block.None; return;
            }
            Block held;
            if (!Map.GetBlockByName(World, id.ToString(), false, out held)) {
                HeldBlock = Block.Stone; return;
            }
            
            if (HeldBlock == held) return;
            HeldBlock = held;
            LastUsedBlockType = held;
            if (Supports(CpeExt.MessageType) && !IsPlayingCTF) {
                Send(Packet.Message((byte)MessageType.BottomRight1, "Block:&f" + Map.GetBlockName(World, HeldBlock)
                                    + " &SID:&f" + (byte)HeldBlock, true));
            }
        }

        void ProcessMovementPacket() {
            BytesReceived += 10;
            UpdateHeldBlock();
            Position newPos = new Position {
                X = reader.ReadInt16(),
                Z = reader.ReadInt16(),
                Y = reader.ReadInt16(),
                R = reader.ReadByte(),
                L = reader.ReadByte()
            };

            Position oldPos = Position;

            // calculate difference between old and new positions
            Position delta = new Position {
                X = (short)( newPos.X - oldPos.X ),
                Y = (short)( newPos.Y - oldPos.Y ),
                Z = (short)( newPos.Z - oldPos.Z ),
                R = (byte)Math.Abs( newPos.R - oldPos.R ),
                L = (byte)Math.Abs( newPos.L - oldPos.L )
            };

            // skip everything if player hasn't moved
            if( delta.IsZero ) return;

            bool posChanged = (delta.X != 0) || (delta.Y != 0) || (delta.Z != 0);
            bool rotChanged = (delta.R != 0) || (delta.L != 0);
            
            //if(rotChanged && !this.isSolid) ResetIdleTimer();
            //if(posChanged && this.isSolid) ResetIdleTimer();
            if (rotChanged) ResetIdleTimer();

            bool deniedzone = false;
            if (World.IsLoaded) { //prevents server error when using genheightmap with large maps
                foreach (Zone zone in World.Map.Zones.Cache) {
                    if (SpecialZone.CheckMoveZone(this, zone, ref deniedzone, newPos)) break;
                }
            }

            if ( Info.IsFrozen || deniedzone) {
                // special handling for frozen players
                if( delta.X * delta.X + delta.Y * delta.Y > AntiSpeedMaxDistanceSquared ||
                    Math.Abs( delta.Z ) > 40 ) {
                    SendNow( Packet.MakeSelfTeleport( Position ) );
                }
                newPos.X = Position.X;
                newPos.Y = Position.Y;
                newPos.Z = Position.Z;

                // recalculate deltas
                delta.X = 0;
                delta.Y = 0;
                delta.Z = 0;

            } else if( !Can( Permission.UseSpeedHack ) || !Info.AllowSpeedhack) {
                int distSquared = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
                // speedhack detection
                if( DetectMovementPacketSpam() ) {
                    return;

                } else if( ( distSquared - delta.Z * delta.Z > AntiSpeedMaxDistanceSquared ||
                             delta.Z > AntiSpeedMaxJumpDelta ) &&
                           speedHackDetectionCounter >= 0 ) {

                    if( speedHackDetectionCounter == 0 ) {
                        lastValidPosition = Position;
                    } else if( speedHackDetectionCounter > 1 ) {
                        DenyMovement();
                        speedHackDetectionCounter = 0;
                        return;
                    }
                    speedHackDetectionCounter++;

                } else {
                    speedHackDetectionCounter = 0;
                }
            }

            if( RaisePlayerMovingEvent( this, newPos ) ) {
                DenyMovement();
                return;
            }

            Position = newPos;
            RaisePlayerMovedEvent( this, oldPos );
        }

        void ProcessSetBlockPacket() {
            BytesReceived += 9;
            if( World == null || World.Map == null ) return;
            if (IsAFK) {
                Server.Players.CanSee(this).Message("{0} is no longer AFK", Name);
                Message("&SYou are no longer AFK");
                IsAFK = false;
                oldafkMob = afkMob;
                afkMob = Info.Mob;
                Server.UpdateTabList(true);
            }
            ResetIdleTimer();
            short x = reader.ReadInt16();
            short z = reader.ReadInt16();
            short y = reader.ReadInt16();
            ClickAction action = ( reader.ReadByte() == 1 ) ? ClickAction.Build : ClickAction.Delete;
            byte type = reader.ReadByte();

            // if a player is using InDev or SurvivalTest client, they may try to
            // place blocks that are not found in MC Classic. Convert them!
            if( type > (byte)Map.MaxCustomBlockType && !Supports(CpeExt.BlockDefinitions)) {
                type = MapDat.MapBlock( type );
            }
            Vector3I coords = new Vector3I( x, y, z );

            // If block is in bounds, count the click.
            // Sometimes MC allows clicking out of bounds,
            // like at map transitions or at the top layer of the world.
            // Those clicks should be simply ignored.
            if( !World.Map.InBounds( coords ) ) return;
            
            if ((action == ClickAction.Delete || type == 0) && !World.Deletable) {
                SendNow(Packet.Message(0, "Deleting blocks is disabled in this world.", false));
                RevertBlockNow(coords); 
                return;
            } else if (action == ClickAction.Build && !World.Buildable) {
                SendNow(Packet.Message(0, "Placing blocks is disabled in this world.", false));
                RevertBlockNow(coords); 
                return;
            }
            
            PlaceBlockWithEvents( coords, action, (Block)type );
        }

        void ProcessPlayerClickPacket() {
            BytesReceived += 15;
            byte button = reader.ReadByte();
            byte action = reader.ReadByte();
            short pitch = reader.ReadInt16();
            short yaw = reader.ReadInt16();
            byte targetEntityID = reader.ReadByte();
            short targetBlockX = reader.ReadInt16();
            short targetBlockY = reader.ReadInt16();
            short targetBlockZ = reader.ReadInt16();
            byte targetBlockFace = reader.ReadByte();
        }


        void Disconnect() {
            State = SessionState.Disconnected;
            Server.RaiseSessionDisconnectedEvent( this, LeaveReason );

            if( HasRegistered ) {
                if( unregisterOnKick )
                    Server.UnregisterPlayer( this );
                RaisePlayerDisconnectedEvent( this, LeaveReason );
            }

            if( stream != null ) stream.Close();
            if( client != null ) client.Close();
        }

        bool LoginSequence()
        {
            byte opCode = reader.ReadByte();

#if DEBUG_NETWORKING
            Logger.Log( LogType.Trace, "from {0} [{1}] {2}", IP, outPacketNumber++, (OpCode)opCode );
#endif
            if (!HandleOpcode(opCode)) return false;

            // Check protocol version
            int clientProtocolVersion = reader.ReadByte();
            if (clientProtocolVersion != Config.ProtocolVersion)
            {
                Logger.Log(LogType.Error,
                            "Player.LoginSequence: Wrong protocol version: {0}.",
                            clientProtocolVersion);
                KickNow("Incompatible protocol version!", LeaveReason.ProtocolViolation);
                return false;
            }

            string givenName = reader.ReadString();
            string verificationCode = reader.ReadString();
            bool supportsCpe = (reader.ReadByte() == 0x42);
            BytesReceived += 131;

            bool isEmailAccount = false;
            if (IsValidEmail(givenName)) {
                isEmailAccount = true;
            }
            else if (!IsValidAccountName(givenName)) {
                // Neither Mojang nor a normal account -- kick it!
                Logger.Log(LogType.SuspiciousActivity,
                            "Player.LoginSequence: Unacceptable player name: {0} ({1})",
                            givenName,
                            IP);
                KickNow("Unacceptable player name!", LeaveReason.ProtocolViolation);
                return false;
            }

            Info = PlayerDB.FindOrCreateInfoForPlayer( givenName, IP );
            ResetAllBinds();
            if (isEmailAccount) {
                Logger.Log( LogType.SystemActivity,
                            "Mojang account <{0}> connected as {1}",
                            givenName,
                            Info.Name );
            }
            if (Server.VerifyName(givenName, verificationCode, Heartbeat.Salt))
            {
                IsVerified = true;
                // update capitalization of player's name
                if (!Info.Name.Equals(givenName, StringComparison.Ordinal))
                {
                    Info.Name = givenName;
                }

            }
            else
            {
                NameVerificationMode nameVerificationMode = ConfigKey.VerifyNames.GetEnum<NameVerificationMode>();

                string standardMessage =
                    String.Format("Player.LoginSequence: Could not verify player name for {0} ({1}).",
                                   Name, IP);
                if (IP.Equals(IPAddress.Loopback) && nameVerificationMode != NameVerificationMode.Always)
                {
                    Logger.Log(LogType.SuspiciousActivity,
                                "{0} Player was identified as connecting from localhost and allowed in.",
                                standardMessage);
                    IsVerified = true;

                }
                else if (IP.IsLocal() && ConfigKey.AllowUnverifiedLAN.Enabled())
                {
                    Logger.Log(LogType.SuspiciousActivity,
                                "{0} Player was identified as connecting from LAN and allowed in.",
                                standardMessage);
                    IsVerified = true;

                }
                else if (Info.TimesVisited > 1 && Info.LastIP.Equals(IP))
                {
                    switch (nameVerificationMode)
                    {
                        case NameVerificationMode.Always:
                            Info.ProcessFailedLogin(this);
                            Logger.Log(LogType.SuspiciousActivity,
                                        "{0} IP matched previous records for that name. " +
                                        "Player was kicked anyway because VerifyNames is set to Always.",
                                        standardMessage);
                            KickNow("Could not verify player name!", LeaveReason.UnverifiedName);
                            return false;

                        case NameVerificationMode.Balanced:
                        case NameVerificationMode.Never:
                            Logger.Log(LogType.SuspiciousActivity,
                                        "{0} IP matched previous records for that name. Player was allowed in.",
                                        standardMessage);
                            IsVerified = true;
                            break;
                    }

                }
                else
                {
                    switch (nameVerificationMode)
                    {
                        case NameVerificationMode.Always:
                        case NameVerificationMode.Balanced:
                            Info.ProcessFailedLogin(this);
                            Logger.Log(LogType.SuspiciousActivity,
                                        "{0} IP did not match. Player was kicked.",
                                        standardMessage);
                            KickNow("Could not verify player name!", LeaveReason.UnverifiedName);
                            return false;

                        case NameVerificationMode.Never:
                            Logger.Log(LogType.SuspiciousActivity,
                                        "{0} IP did not match. Player was allowed in anyway because VerifyNames is set to Never.",
                                        standardMessage);
                            Message("&WYour name could not be verified.");
                            break;
                    }
                }
            }

            // Check if player is banned
            if (Info.IsBanned)
            {
                Info.ProcessFailedLogin(this);
                Logger.Log(LogType.SuspiciousActivity,
                            "Banned player {0} tried to log in from {1}",
                            Name, IP);
                string bannedMessage;
                if (Info.BannedBy != null)
                {
                    if (Info.BanReason != null)
                    {
                        bannedMessage = String.Format("Banned {0} ago by {1}: {2}",
                                                       Info.TimeSinceBan.ToMiniString(),
                                                       Info.BannedBy,
                                                       Info.BanReason);
                    }
                    else
                    {
                        bannedMessage = String.Format("Banned {0} ago by {1}",
                                                       Info.TimeSinceBan.ToMiniString(),
                                                       Info.BannedBy);
                    }
                }
                else
                {
                    if (Info.BanReason != null)
                    {
                        bannedMessage = String.Format("Banned {0} ago: {1}",
                                                       Info.TimeSinceBan.ToMiniString(),
                                                       Info.BanReason);
                    }
                    else
                    {
                        bannedMessage = String.Format("Banned {0} ago",
                                                       Info.TimeSinceBan.ToMiniString());
                    }
                }
                KickNow(bannedMessage, LeaveReason.LoginFailed);
                return false;
            }


            // Check if player's IP is banned
            IPBanInfo ipBanInfo = IPBanList.Get(IP);
            if (ipBanInfo != null && Info.BanStatus != BanStatus.IPBanExempt)
            {
                Info.ProcessFailedLogin(this);
                ipBanInfo.ProcessAttempt(this);
                Logger.Log(LogType.SuspiciousActivity,
                            "{0} tried to log in from a banned IP.", Name);
                string bannedMessage = String.Format("IP-banned {0} ago by {1}: {2}",
                                                      DateTime.UtcNow.Subtract(ipBanInfo.BanDate).ToMiniString(),
                                                      ipBanInfo.BannedBy,
                                                      ipBanInfo.BanReason);
                KickNow(bannedMessage, LeaveReason.LoginFailed);
                return false;
            }


            // Check if player is paid (if required)
            if (ConfigKey.PaidPlayersOnly.Enabled() && Info.AccountType != AccountType.Paid)
            {
                SendNow(Packet.MakeHandshake(this,
                                               ConfigKey.ServerName.GetString(),
                                               "Please wait; Checking paid status..."));
                writer.Flush();

                Info.AccountType = CheckPaidStatus(Name);
                if (Info.AccountType != AccountType.Paid)
                {
                    Logger.Log(LogType.SystemActivity,
                                "Player {0} was kicked because their account is not paid, and PaidPlayersOnly setting is enabled.",
                                Name);
                    KickNow("Paid players allowed only.", LeaveReason.LoginFailed);
                    return false;
                }
            }
            else
            {
                Info.CheckAccountType();
            }


            // Any additional security checks should be done right here
            if (RaisePlayerConnectingEvent(this)) return false;


            // ----==== beyond this point, player is considered connecting (allowed to join) ====----


            // negotiate protocol extensions
            if (supportsCpe && !NegotiateProtocolExtension())
            {
                return false;
            }

            if (string.IsNullOrEmpty(ClientName) || !ClientName.ToLower().Contains("classicalsharp")) {
                Message("&bIt is recommended that you use the ClassicalSharp client!");
                Message("&9http://123dmwm.tk/cs &bredirects to the official download.");
            }


            // Register player for future block updates
            if (!Server.RegisterPlayer(this))
            {
                Logger.Log(LogType.SystemActivity,
                            "Player {0} was kicked because server is full.", Name);
                string kickMessage = String.Format("Sorry, server is full ({0}/{1})",
                                                    Server.Players.Length, ConfigKey.MaxPlayers.GetInt());
                KickNow(kickMessage, LeaveReason.ServerFull);
                return false;
            }
            Info.ProcessLogin(this);
            State = SessionState.LoadingMain;


            // ----==== Beyond this point, player is considered connected (authenticated and registered) ====----
            Logger.Log(LogType.UserActivity, "{0} &Sconnected from {1}.", Name, IP);


            // Figure out what the starting world should be
            World startingWorld = WorldManager.FindMainWorld(this);
            startingWorld = RaisePlayerConnectedEvent(this, startingWorld);
            Position = startingWorld.LoadMap().getSpawnIfRandom();

            // Send server information
            string serverName = ConfigKey.ServerName.GetString();
            string motd = "Welcome to our server!";
            FileInfo MOTDInfo = new FileInfo("./MOTDList.txt");
            if (MOTDInfo.Exists)
            {
                string[] MOTDlist = File.ReadAllLines("./MOTDList.txt");
                Array.Sort(MOTDlist);
                Random random = new Random();
                int index = random.Next(0, MOTDlist.Length);
                motd = MOTDlist[index];
                if (motd.Length > 64) {
                    motd = "&0=&c=&e= Welcome to our server! &e=&c=&0=";
                } else {
                    LastMotdMessage = motd;
                    motd = "&0=&c=&e= " + motd + " &e=&c=&0=";
                }
            }
            SendNow(Packet.MakeHandshake(this, serverName, motd));

            // AutoRank
            if (ConfigKey.AutoRankEnabled.Enabled())
            {
                Rank newRank = AutoRankManager.Check(Info);
                if (newRank != null)
                {
                    try
                    {
                        Info.ChangeRank(AutoRank, newRank, "~AutoRank", true, true, true);
                    }
                    catch (PlayerOpException ex)
                    {
                        Logger.Log(LogType.Error,
                                    "AutoRank failed on player {0}: {1}",
                                    ex.Player.Name, ex.Message);
                    }
                }
            }

            bool firstTime = (Info.TimesVisited == 1);
            if (!JoinWorldNow(startingWorld, true, WorldChangeReason.FirstWorld))
            {
                Logger.Log(LogType.Warning,
                            "Could not load main world ({0}) for connecting player {1} (from {2}): " +
                            "Either main world is full, or an error occurred.",
                            startingWorld.Name, Name, IP);
                KickNow("Either main world is full, or an error occurred.", LeaveReason.WorldFull);
                return false;
            }


            // ==== Beyond this point, player is considered ready (has a world) ====

            var canSee = Server.Players.CanSee(this).ToArray();

            // Announce join
            if (ConfigKey.ShowConnectionMessages.Enabled())
            {
                string message = Server.MakePlayerConnectedMessage(this, firstTime);
                canSee.Message(message);
            }

            if (!IsVerified)
            {
                canSee.Message("&WName and IP of {0}&W are unverified!", ClassyName);
            }

            if (Info.IsHidden)
            {
                if (Can(Permission.Hide))
                {
                    canSee.Message("&8Player {0}&8 logged in hidden.", ClassyName);
                }
                else
                {
                    Info.IsHidden = false;
                }
            }
            Info.GeoipLogin();

            // Check if other banned players logged in from this IP
            PlayerInfo[] bannedPlayerNames = PlayerDB.FindPlayers(IP, 25)
                                                     .Where(playerFromSameIP => playerFromSameIP.IsBanned)
                                                     .ToArray();
            if (bannedPlayerNames.Length > 0)
            {
                canSee.Message("&WPlayer {0}&W logged in from an IP shared by banned players: {1}",
                                ClassyName, bannedPlayerNames.JoinToClassyString());
                Logger.Log(LogType.SuspiciousActivity,
                            "Player {0} logged in from an IP shared by banned players: {1}",
                            ClassyName, bannedPlayerNames.JoinToString(info => info.Name));
            }

            // check if player is still muted
            if (Info.MutedUntil > DateTime.UtcNow)
            {
                Message("&WYou were previously muted by {0}&W, {1} left.",
                         Info.MutedByClassy, Info.TimeMutedLeft.ToMiniString());
                canSee.Message("&WPlayer {0}&W was previously muted by {1}&W, {2} left.",
                                ClassyName, Info.MutedByClassy, Info.TimeMutedLeft.ToMiniString());
            }

            // check if player is still frozen
            if (Info.IsFrozen)
            {
                if (Info.FrozenOn != DateTime.MinValue)
                {
                    Message("&WYou were previously frozen {0} ago by {1}. This means you can not move or place/delete blocks. Seek Moderator Assistance.",
                             Info.TimeSinceFrozen.ToMiniString(),
                             Info.FrozenByClassy);
                    canSee.Message("&WPlayer {0}&W was previously frozen {1} ago by {2}",
                                    ClassyName,
                                    Info.TimeSinceFrozen.ToMiniString(),
                                    Info.FrozenByClassy);
                }
                else
                {
                    Message("&WYou were previously frozen by {0}. This means you can not move or place/delete blocks. Seek Moderator Assistance.",
                             Info.FrozenByClassy);
                    canSee.Message("&WPlayer {0}&W was previously frozen by {1}.",
                                    ClassyName, Info.FrozenByClassy);
                }
            }

            // Welcome message
            if (File.Exists(Paths.GreetingFileName))
            {
                string[] greetingText = File.ReadAllLines(Paths.GreetingFileName);
                foreach (string greetingLine in greetingText)
                {
                    Message(Chat.ReplaceTextKeywords(this, greetingLine));
                }
            }
            else
            {
                if (firstTime)
                {
                    Message("Welcome to {0}", ConfigKey.ServerName.GetString());
                }
                else
                {
                    Message("Welcome back to {0}", ConfigKey.ServerName.GetString());
                }

                Message("Your rank is {0}&S. Type &H/Help&S for help.",
                            Info.Rank.ClassyName);
            }
            if (Info.Rank == RankManager.HighestRank) {
                if (Chat.Reports.Count() >= 1) {
                    Message(Chat.Reports.Count() + " unread /Reports");
                }
            }

            // A reminder for first-time users
            if (PlayerDB.Size == 1 && Info.Rank != RankManager.HighestRank)
            {
                Message("Type &H/Rank {0} {1}&S in console to promote yourself",
                         Name, RankManager.HighestRank.Name);
            }
            
            MaxCopySlots = Info.Rank.CopySlots;

            HasFullyConnected = true;
            State = SessionState.Online;

            // Add player to the userlist
            Server.UpdatePlayerList();
            RaisePlayerReadyEvent(this);

            if (Supports(CpeExt.MessageType))
                Send(Packet.Message((byte)MessageType.Status1, ConfigKey.ServerName.GetString(), UseFallbackColors));

            short NID = 1;
            this.NameID = NID;
            retry:
            foreach (Player player in Server.Players)
            {
                if (this.NameID == player.NameID && this.Info.PlayerObject != player.Info.PlayerObject)
                {
                    this.NameID++;
                    goto retry;
                }
            }
            if (Info.skinName == "") {
                oldskinName = Info.skinName;
                Info.skinName = Name;
            }
            Send(Packet.MakeSetPermission(this));
            Server.UpdateTabList(true);
            System.Console.Beep();
            System.Console.Beep();
            return true;
        }


        #region Joining Worlds

        readonly object joinWorldLock = new object();

        [CanBeNull] World forcedWorldToJoin;
        WorldChangeReason worldChangeReason;
        Position postJoinPosition;
        bool useWorldSpawn;


        public void JoinWorld( [NotNull] World newWorld, WorldChangeReason reason ) {
            if( newWorld == null ) throw new ArgumentNullException( "newWorld" );
            lock( joinWorldLock ) {
                useWorldSpawn = true;
                postJoinPosition = Position.Zero;
                forcedWorldToJoin = newWorld;
                worldChangeReason = reason;
            }
        }


        public void JoinWorld( [NotNull] World newWorld, WorldChangeReason reason, Position position ) {
            if( newWorld == null ) throw new ArgumentNullException( "newWorld" );
            if( !Enum.IsDefined( typeof( WorldChangeReason ), reason ) ) {
                throw new ArgumentOutOfRangeException( "reason" );
            }
            lock( joinWorldLock ) {
                useWorldSpawn = false;
                postJoinPosition = position;
                forcedWorldToJoin = newWorld;
                worldChangeReason = reason;
            }
        }


        internal bool JoinWorldNow([NotNull] World newWorld, bool doUseWorldSpawn, WorldChangeReason reason) {
            if (newWorld == null) throw new ArgumentNullException("newWorld");
            if (!Enum.IsDefined(typeof(WorldChangeReason), reason)) {
                throw new ArgumentOutOfRangeException("reason");
            }
            /*if (Thread.CurrentThread != ioThread)
            {
                throw new InvalidOperationException(
                    "Player.JoinWorldNow may only be called from player's own thread. " +
                    "Use Player.JoinWorld instead.");
            }*/


            if (!StandingInPortal) {
                LastWorld = World;
                LastPosition = Position;
            }

            string textLine1 = "Loading world " + newWorld.ClassyName;
            string textLine2 = newWorld.MOTD ?? "Welcome!";

            if (RaisePlayerJoiningWorldEvent(this, newWorld, reason, textLine1, textLine2)) {
                Logger.Log(LogType.Warning,
                            "Player.JoinWorldNow: Player {0} was prevented from joining world {1} by an event callback.",
                            Name, newWorld.Name);
                return false;
            }

            World oldWorld = World;

            // remove player from the old world
            if (oldWorld != null && oldWorld != newWorld) {
                if (!oldWorld.ReleasePlayer(this)) {
                    Logger.Log(LogType.Error,
                                "Player.JoinWorldNow: Player asked to be released from its world, " +
                                "but the world did not contain the player.");
                }
            }

            lock (entitiesLock)
                ResetVisibleEntities();
            ClearQueue(blockQueue);
            RemoveOldEntities(oldWorld);
            Map map;

            // try to join the new world
            if (oldWorld != newWorld) {
                bool announce = (oldWorld != null) && (oldWorld.Name != newWorld.Name);
                map = newWorld.AcceptPlayer(this, announce);
                if (map == null)
                    return false;
            } else {
                map = newWorld.LoadMap();
            }

            World = newWorld;
            // Set spawn point
            Position = doUseWorldSpawn ? map.getSpawnIfRandom() : postJoinPosition;

            // Start sending over the level copy
            if (oldWorld != null) {
                SendNow(Packet.MakeHandshake(this, textLine1, textLine2));
            }
            // needs to be sent before the client receives the map data
            if (Supports(CpeExt.BlockDefinitions)) {
            	if (oldWorld != null)
            	    BlockDefinition.SendNowRemoveOldBlocks(this, oldWorld);
                BlockDefinition.SendNowBlocks(this);
            }
            if (Supports(CpeExt.BlockPermissions))
                SendBlockPermissions();

            writer.Write(OpCode.MapBegin);
            BytesSent++;

            // enable Nagle's algorithm (in case it was turned off by LowLatencyMode)
            // to avoid wasting bandwidth for map transfer
            client.NoDelay = false;
            WriteWorldData(map);
            // Turn off Nagel's algorithm again for LowLatencyMode
            client.NoDelay = ConfigKey.LowLatencyMode.Enabled();

            // Done sending over level copy
            writer.Write(OpCode.MapEnd);
            writer.Write((short)map.Width);
            writer.Write((short)map.Height);
            writer.Write((short)map.Length);
            BytesSent += 7;

            SendJoinCpeExtensions();
            SendNewEntities(newWorld);

            // Teleport player to the target location
            // This allows preserving spawn rotation/look, and allows
            // teleporting player to a specific location (e.g. /TP or /Bring)
            writer.Write(Packet.MakeTeleport(Packet.SelfId, Position).Bytes);
            BytesSent += 10;

            SendJoinMessage(oldWorld, newWorld);
            RaisePlayerJoinedWorldEvent(this, oldWorld, reason);
            if (newWorld.map.Spawn == new Position(-1, -1, -1, 0, 0) && oldWorld != newWorld) {
                Message("Randomized Spawn!");
            }

            Server.UpdateTabList(true);
            Server.RequestGC();
            return true;
        }
        
        void WriteWorldData(Map map) {
             // Transfer compressed map copy
            Block maxLegal = supportsCustomBlocks ? Map.MaxCustomBlockType : Map.MaxLegalBlockType;
            Logger.Log(LogType.Debug, "Player.JoinWorldNow: Sending compressed map to {0}.", Name);
            
            if (supportsCustomBlocks && supportsBlockDefs)
                map.CompressMap(this);
            else
            	map.CompressAndConvertMap((byte)maxLegal, this);
        }
        
        void SendJoinMessage(World oldWorld, World newWorld) {
            if (oldWorld == newWorld)
            {
                Message("&SRejoined world {0}", newWorld.ClassyName);
            }
            else
            {
                Message("&SJoined world {0}", newWorld.ClassyName);
                string greeting = newWorld.Greeting;
                if (greeting != null)
                {
                    greeting = Chat.ReplaceTextKeywords(this, greeting);
                    Message(greeting);
                }
                else
                {
                    FileInfo GreetingInfo = new FileInfo("./WorldGreeting/" + World.Name + ".txt");
                    if (GreetingInfo.Exists)
                    {
                        string[] Greeting = File.ReadAllLines("./WorldGreeting/" + World.Name + ".txt");
                        string GreetingMessage = "";
                        foreach (string line in Greeting)
                        {
                            GreetingMessage += line + "&N";
                        }
                        Message(Chat.ReplaceTextKeywords(this, GreetingMessage));
                    }
                }
            }
        }
        
        void SendJoinCpeExtensions() {
            SendEnvSettings();
            if (Supports(CpeExt.ExtPlayerList2)) {
                SendNow(Packet.MakeExtAddEntity2(Packet.SelfId, Info.Rank.Color + Name, 
            	                                 (Info.skinName == "" ? Name : Info.skinName), Position, this));
            } else {
                SendNow(Packet.MakeAddEntity(Packet.SelfId, Info.Rank.Color + Name, Position));
            }

            if (Supports(CpeExt.ChangeModel)) {
                SendNow(Packet.MakeChangeModel(255, !IsAFK ? Info.Mob : AFKModel));
            }

            if (Supports(CpeExt.HackControl)) {
                SendNow(PlayerHacks.MakePacket(this, World.MOTD));
            }

            if (Supports(CpeExt.ClickDistance)) {
                short reach = (World.maxReach < Info.ReachDistance && !IsStaff) ? World.maxReach : Info.ReachDistance;
                SendNow(Packet.MakeSetClickDistance(reach));
            }

            if (Supports(CpeExt.MessageType) && !IsPlayingCTF) {
                SendNow(Packet.Message((byte)MessageType.BottomRight1, "Block:&f" + Map.GetBlockName(World, HeldBlock)
                                    + " &SID:&f" + (byte)HeldBlock, true));
            }
            if (Supports(CpeExt.MessageType)) {
                SendNow(Packet.Message((byte)MessageType.Status1, ConfigKey.ServerName.GetString(), UseFallbackColors));
            }

            if (Supports(CpeExt.SelectionCuboid)) {
                foreach (Zone z in WorldMap.Zones) {
                    if (z.ShowZone)
                        SendNow(Packet.MakeMakeSelection(z.ZoneID, z.Name, z.Bounds, z.Color, z.Alpha));
                }
            }
        }
        
        internal void RemoveOldEntities(World world) {
            if (world == null) return;
            foreach (Entity entity in Entity.Entities.Where(e => Entity.getWorld(e) == world)) {
                SendNow(Packet.MakeRemoveEntity(entity.ID));
            } 
        }
        
        internal void SendNewEntities(World world) {
            foreach (Entity entity in Entity.Entities.Where(e => Entity.getWorld(e) == world)) {
                if (Supports(CpeExt.ExtPlayerList2)) {
                    SendNow(Packet.MakeExtAddEntity2(entity.ID, entity.Name, entity.Skin, Entity.getPos(entity), this));
                } else {
                    SendNow(Packet.MakeAddEntity(entity.ID, entity.Name, Entity.getPos(entity)));
                }
        		if (!entity.Model.CaselessEquals("humanoid") && Supports(CpeExt.ChangeModel))
                    SendNow(Packet.MakeChangeModel((byte)entity.ID, entity.Model));
            }
        }
        
        internal void SendBlockPermissions() {
            Block max = supportsCustomBlocks ? Map.MaxCustomBlockType : Map.MaxLegalBlockType;
            bool build = World.Buildable, delete = World.Deletable;
            for (Block block = Block.Stone; block <= max; block++) {
                Send(Packet.MakeSetBlockPermission(block, build, delete));
            }
            
            Send(Packet.MakeSetBlockPermission(Block.Admincrete, 
                build && Can(Permission.PlaceAdmincrete), delete && Can(Permission.PlaceAdmincrete)));
            Send(Packet.MakeSetBlockPermission(
                Block.Water, build && Can(Permission.PlaceWater), delete));
            Send(Packet.MakeSetBlockPermission(
                Block.StillWater, build && Can(Permission.PlaceWater), delete));
            Send(Packet.MakeSetBlockPermission(
                Block.Lava, build && Can(Permission.PlaceLava), delete));
            Send(Packet.MakeSetBlockPermission(
                Block.StillLava, build && Can(Permission.PlaceLava), delete));
            Send(Packet.MakeSetBlockPermission(
                Block.Grass, build && Can(Permission.PlaceGrass), delete));
            
            if (!supportsBlockDefs) return;
            BlockDefinition[] defs = World.BlockDefs;
            for (int i = (int)Map.MaxCustomBlockType + 1; i < defs.Length; i++) {
                if (defs[i] == null) continue;
                Send(Packet.MakeSetBlockPermission((Block)i, build, delete));
            }
        }
        
        internal void SendEnvSettings() {
            byte side = World.EdgeBlock, edge = World.HorizonBlock;
            CheckBlock(ref side);
            CheckBlock(ref edge);
            
            if (Supports(CpeExt.EnvMapAspect)) {
                Send(Packet.MakeEnvSetMapUrl(World.GetTexture()));
                Send(Packet.MakeEnvSetMapProperty(EnvProp.SidesBlock, side));
                Send(Packet.MakeEnvSetMapProperty(EnvProp.EdgeBlock, edge));
                Send(Packet.MakeEnvSetMapProperty(EnvProp.EdgeLevel, World.GetEdgeLevel()));
                Send(Packet.MakeEnvSetMapProperty(EnvProp.CloudsLevel, World.GetCloudsHeight()));
                Send(Packet.MakeEnvSetMapProperty(EnvProp.MaxFog, World.MaxFogDistance));
                Send(Packet.MakeEnvSetMapProperty(EnvProp.CloudsSpeed, World.CloudsSpeed));
                Send(Packet.MakeEnvSetMapProperty(EnvProp.WeatherSpeed, World.WeatherSpeed));
                Send(Packet.MakeEnvSetMapProperty(EnvProp.WeatherFade, World.WeatherFade));
            } else if (Supports(CpeExt.EnvMapAppearance2)) {
                Send(Packet.MakeEnvSetMapAppearance2(World.GetTexture(), side, edge, World.GetEdgeLevel(),
                                                     World.GetCloudsHeight(), World.MaxFogDistance));
            } else if (Supports(CpeExt.EnvMapAppearance)) {
                Send(Packet.MakeEnvSetMapAppearance(World.GetTexture(), side, edge, World.GetEdgeLevel()));
            }

            if (Supports(CpeExt.EnvColors)) {
                Send(Packet.MakeEnvSetColor((byte)EnvVariable.SkyColor, World.SkyColor));
                Send(Packet.MakeEnvSetColor((byte)EnvVariable.CloudColor, World.CloudColor));
                Send(Packet.MakeEnvSetColor((byte)EnvVariable.FogColor, World.FogColor));
                Send(Packet.MakeEnvSetColor((byte)EnvVariable.Shadow, World.ShadowColor));
                Send(Packet.MakeEnvSetColor((byte)EnvVariable.Sunlight, World.LightColor));
            }

            if (Supports(CpeExt.EnvWeatherType)) {
                Send(Packet.SetWeather((byte)WeatherType.Sunny));
                Send(Packet.SetWeather(World.Weather));
            }
        }
        
        bool GetHacksFromMotd(out bool canFly, out bool canNoClip, out bool canSpeed, out bool canRespawn) {
            bool useMotd = false;
            canFly = false; canNoClip = false; canSpeed = false; canRespawn = false;
            if (String.IsNullOrEmpty(World.MOTD)) return false;
            
            foreach (string s in World.MOTD.ToLower().Split()) {
                switch (s) {
                    case "-fly":
                    case "+fly":
                        canFly = s == "+fly";
                        useMotd = true;
                        break;
                    case "-noclip":
                    case "+noclip":
                        canNoClip = s == "+noclip";
                        useMotd = true;
                        break;
                    case "-speed":
                    case "+speed":
                        canSpeed = s == "+speed";
                        useMotd = true;
                        break;
                    case "-respawn":
                    case "+respawn":
                        canRespawn = s == "+respawn";
                        useMotd = true;
                        break;
                    case "-hax":
                    case "+hax":
                        canFly = s == "+hax";
                        canNoClip = s == "+hax";
                        canSpeed = s == "+hax";
                        canRespawn = s == "+hax";
                        useMotd = true;
                        break;
                    case "+ophax":
                        canFly = IsStaff;
                        canNoClip = IsStaff;
                        canSpeed = IsStaff;
                        canRespawn = IsStaff;
                        useMotd = true;
                        break;
                    default:
                        break;
                }
            }
            return useMotd;
        }

        #endregion


        #region Sending

        /// <summary> Send packet to player (not thread safe, sync, immediate).
        /// Should NEVER be used from any thread other than this session's ioThread.
        /// Not thread-safe (for performance reason). </summary>
        public void SendNow( Packet packet ) {
            if( Thread.CurrentThread != ioThread ) {
                throw new InvalidOperationException( "SendNow may only be called from player's own thread." );
            }
            if (packet.OpCode == OpCode.SetBlockServer)
                CheckBlock(ref packet.Bytes[7]);
            writer.Write( packet.Bytes );
            BytesSent += packet.Bytes.Length;
        }


        /// <summary> Send packet (thread-safe, async, priority queue).
        /// This is used for most packets (movement, chat, etc). </summary>
        public void Send(Packet packet) {
            if (packet.OpCode == OpCode.SetBlockServer)
                CheckBlock(ref packet.Bytes[7]);
            if( canQueue ) priorityOutputQueue.Enqueue( packet );
        }
        
        
        /// <summary> Sends a block change to THIS PLAYER ONLY 
        /// (thread-safe, asynchronous, delayed queue). Does not affect the map. </summary>
        /// <param name="coords"> Coordinates of the block. </param>
        /// <param name="block"> Block type to send. </param>
        public void SendBlock( Vector3I coords, Block block ) {
            if( !WorldMap.InBounds( coords ) ) throw new ArgumentOutOfRangeException( "coords" );
            byte raw = (byte)block;
            CheckBlock( ref raw );
            if( canQueue ) blockQueue.Enqueue( new SetBlockData( coords, raw ) );
        }


        /// <summary> Gets the block from given location in player's world,
        /// and sends it (thread-safe, asynchronous, delayed queue) to the player.
        /// Used to undo player's attempted block placement/deletion. </summary>
        public void RevertBlock( Vector3I coords ) {
            byte raw = (byte)WorldMap.GetBlock( coords );
            CheckBlock( ref raw );
            if( canQueue ) blockQueue.Enqueue( new SetBlockData( coords, raw ) );
        }


        // Gets the block from given location in player's world, and sends it (sync) to the player.
        // Used to undo player's attempted block placement/deletion.
        // To avoid threading issues, only use this from this player's IoThread.
        internal void RevertBlockNow( Vector3I coords ) {
            SendNow( Packet.MakeSetBlock( coords, WorldMap.GetBlock( coords ) ));
        }

        #endregion


        static void ClearQueue<T>([NotNull] ConcurrentQueue<T> queue) {
            if (queue == null) throw new ArgumentNullException("queue");
            T ignored;
            while (queue.TryDequeue(out ignored)) { }
        }           


        #region Kicking

        /// <summary> Kick (asynchronous). Immediately blocks all client input, but waits
        /// until client thread has sent the kick packet. </summary>
        public void Kick( [NotNull] string message, LeaveReason leaveReason, bool unregister = true ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( !Enum.IsDefined( typeof( LeaveReason ), leaveReason ) ) {
                throw new ArgumentOutOfRangeException( "leaveReason" );
            }
            State = SessionState.PendingDisconnect;
            LeaveReason = leaveReason;

            canReceive = false;
            canQueue = false;
            unregisterOnKick = unregister;

            // clear all pending output to be written to client (it won't matter after the kick)
            ClearQueue(blockQueue);
            ClearQueue(priorityOutputQueue);

            // bypassing Send() because canQueue is false
            priorityOutputQueue.Enqueue( Packet.MakeKick( message ) );
        }

        /// <summary> Kick (synchronous). Immediately sends the kick packet.
        /// Can only be used from IoThread (this is not thread-safe). </summary>
        void KickNow( [NotNull] string message, LeaveReason leaveReason ) {
            if( message == null ) throw new ArgumentNullException( "message" );
            if( Thread.CurrentThread != ioThread ) {
                throw new InvalidOperationException( "KickNow may only be called from player's own thread." );
            }
            State = SessionState.PendingDisconnect;
            LeaveReason = leaveReason;

            canQueue = false;
            canReceive = false;
            canSend = false;
            SendNow( Packet.MakeKick( message ) );
            writer.Flush();
        }


        /// <summary> Blocks the calling thread until this session disconnects. </summary>
        public void WaitForDisconnect() {
            if( Thread.CurrentThread == ioThread ) {
                throw new InvalidOperationException( "Cannot call WaitForDisconnect from IoThread." );
            }
            if( ioThread != null && ioThread.IsAlive ) {
                try {
                    ioThread.Join();
                } catch( NullReferenceException ) {
                } catch( ThreadStateException ) {}
            }
        }

        #endregion


        #region Movement

        // visible entities
        public readonly object entitiesLock = new object();
        public readonly Dictionary<Player, VisibleEntity> entities = new Dictionary<Player, VisibleEntity>();
        readonly Stack<Player> playersToRemove = new Stack<Player>( 127 );
        readonly Stack<sbyte> freePlayerIDs = new Stack<sbyte>( 127 );

        // movement optimization
        int fullUpdateCounter;
        public const int FullPositionUpdateIntervalDefault = 20;
        public static int FullPositionUpdateInterval = FullPositionUpdateIntervalDefault;

        const int SkipMovementThresholdSquared = 64,
                  SkipRotationThresholdSquared = 1500;

        // anti-speedhack vars
        int speedHackDetectionCounter;

        const int AntiSpeedMaxJumpDelta = 25,
                  // 16 for normal client, 25 for WoM
                  AntiSpeedMaxDistanceSquared = 1024,
                  // 32 * 32
                  AntiSpeedMaxPacketCount = 200,
                  AntiSpeedMaxPacketInterval = 5;

        // anti-speedhack vars: packet spam
        readonly Queue<DateTime> antiSpeedPacketLog = new Queue<DateTime>();
        DateTime antiSpeedLastNotification = DateTime.UtcNow;

        void ResetVisibleEntities() {
            foreach( var pos in entities.Values ) {
                SendNow( Packet.MakeRemoveEntity( pos.Id ) );
            }
            freePlayerIDs.Clear();
            for( int i = 1; i <= sbyte.MaxValue; i++ ) {
                freePlayerIDs.Push( (sbyte)i );
            }
            playersToRemove.Clear();
            entities.Clear();
        }

        
        void UpdateVisibleEntities() {
            if( World == null ) PlayerOpException.ThrowNoWorld( this );

            // handle following the spectatee
            if( spectatedPlayer != null ) FollowSpectatedEntity();

            // check every player on the current world
            Player[] worldPlayerList = World.Players;
            Position pos = Position;
            
            for( int i = 0; i < worldPlayerList.Length; i++ ) {
                Player otherPlayer = worldPlayerList[i];
                if (otherPlayer.World == null) continue; 
                // rarely occurs when player has been added to the list, but has not yet had their World field set.
                
                // Fetch or create a VisibleEntity object for the player
                VisibleEntity entity;
                if (!otherPlayer.CanSee(this))
                    goto skip;
                if (otherPlayer != this) {
                    entity = CheckEntity(this, otherPlayer);
                } else {
                    entity = new VisibleEntity(Position, -1, Info.Rank);
                }
                CheckOwnChange(entity.Id, otherPlayer);
                
            skip:
                if (otherPlayer == this || !CanSee(otherPlayer)) continue;
                entity = CheckEntity(otherPlayer, this);

                Position otherPos = otherPlayer.Position;
                int distance = pos.DistanceSquaredTo( otherPos );

                // Re-add player if their rank changed (to maintain correct name colors/prefix)
                if( entity.LastKnownRank != otherPlayer.Info.Rank ) {
                    ReAddEntity( entity, otherPlayer );
                    entity.LastKnownRank = otherPlayer.Info.Rank;
                }

                if( entity.Hidden ) {
                    if( distance < entityShowingThreshold && CanSeeMoving( otherPlayer ) ) {
                        ShowEntity( entity, otherPos );
                    }

                } else {
                    if( distance > entityHidingThreshold || !CanSeeMoving( otherPlayer ) ) {
                        HideEntity( entity );

                    } else if( entity.LastKnownPosition != otherPos ) {
                        MoveEntity( entity, otherPos );
                    }
                }

                if (spectatedPlayer == otherPlayer) { //Hide player being spectated
                    HideEntity(entity);
                } else if (otherPlayer.spectatedPlayer == this) { //Hide player spectating you
                    HideEntity(entity);
                } else if (otherPlayer.IsSpectating) { //Is other player spectating?...
                    if (CanSee(otherPlayer)) { //...Update location of player who is able to be seen while hidden
                        MoveEntity(entity, entity.LastKnownPosition);
                    } else { //..Hide other player
                        HideEntity(entity);
                    }
                }
            }
            oldskinName = Info.skinName;
            oldMob = Info.Mob;
            oldafkMob = afkMob;
            lock (entitiesLock)
                RemoveNonRetainedEntities();

            fullUpdateCounter++;
            if( fullUpdateCounter >= FullPositionUpdateInterval ) {
                fullUpdateCounter = 0;
            }
        }
        
        VisibleEntity CheckEntity(Player src, Player other) {
            VisibleEntity entity;
            lock (other.entitiesLock) {
                if (other.entities.TryGetValue(src, out entity))
                    entity.MarkedForRetention = true;
                else
                    entity = other.AddEntity(src);
            }
            return entity;
        }
        
        void RemoveNonRetainedEntities() {
            // Find entities to remove (not marked for retention).
            foreach( var pair in entities ) {
                if( pair.Value.MarkedForRetention ) {
                    pair.Value.MarkedForRetention = false;
                } else {
                    playersToRemove.Push( pair.Key );
                }
            }

            // Remove non-retained entities
            while( playersToRemove.Count > 0 ) {
                RemoveEntity( playersToRemove.Pop() );
            }
        }

        void FollowSpectatedEntity() {
            if( !spectatedPlayer.IsOnline || !CanSee( spectatedPlayer ) ) {
                Message( "Stopped spectating {0}&S (disconnected)", spectatedPlayer.ClassyName );
                spectatedPlayer = null;
                return;
            }
            
            Position spectatePos = spectatedPlayer.Position;
            World spectateWorld = spectatedPlayer.World;
            if( spectateWorld == null ) {
                throw new InvalidOperationException( "Trying to spectate player without a world." );
            }
            if( spectateWorld != World ) {
                if( CanJoin( spectateWorld ) ) {
                    postJoinPosition = spectatePos;
                    if( JoinWorldNow( spectateWorld, false, WorldChangeReason.SpectateTargetJoined ) ) {
                        Message( "Joined {0}&S to continue spectating {1}",
                                spectateWorld.ClassyName,
                                spectatedPlayer.ClassyName );
                    } else {
                        Message( "Stopped spectating {0}&S (cannot join {1}&S)",
                                spectatedPlayer.ClassyName,
                                spectateWorld.ClassyName );
                        spectatedPlayer = null;
                    }
                } else {
                    Message( "Stopped spectating {0}&S (cannot join {1}&S)",
                            spectatedPlayer.ClassyName,
                            spectateWorld.ClassyName );
                    spectatedPlayer = null;
                }
            } else if( spectatePos != Position ) {
                SendNow( Packet.MakeSelfTeleport( spectatePos ) );
            }
            if (SpectatedPlayer.HeldBlock != HeldBlock && SpectatedPlayer.Supports(CpeExt.HeldBlock))
            {
            	byte block = (byte)SpectatedPlayer.HeldBlock;
                CheckBlock(ref block);
                SendNow(Packet.MakeHoldThis((Block)block, false));
            }
        }
        
        void CheckOwnChange(sbyte id, Player otherPlayer) {
            if (oldskinName != Info.skinName && otherPlayer.Supports(CpeExt.ExtPlayerList2)) {
                otherPlayer.Send(Packet.MakeExtAddEntity2(id, Info.Rank.Color + Name,
                                                          (Info.skinName == "" ? Name : Info.skinName), Position, otherPlayer));
                //otherPlayer.Send(Packet.MakeTeleport(id, Position));
                if (otherPlayer.Supports(CpeExt.ChangeModel)) {
                    string thisModel = IsAFK ? AFKModel : Info.Mob;
                    if (otherPlayer.Info.Rank.CanSee(Info.Rank) && (thisModel.CaselessEquals("air") || thisModel.CaselessEquals("0"))) {
                        thisModel = "Humanoid";
                    }
                    otherPlayer.Send(Packet.MakeChangeModel((byte)id, thisModel));
                }
            }
            if ((oldMob != Info.Mob || oldafkMob != afkMob) && otherPlayer.Supports(CpeExt.ChangeModel)) {
                string thisModel = IsAFK ? AFKModel : Info.Mob;
                if (otherPlayer.Info.Rank.CanSee(Info.Rank) && (thisModel.CaselessEquals("air") || thisModel.CaselessEquals("0"))) {
                    thisModel = "Humanoid";
                }
                otherPlayer.Send(Packet.MakeChangeModel((byte)id, thisModel));
            }
        }
        
        VisibleEntity AddEntity( [NotNull] Player player ) {
            if( player == null ) throw new ArgumentNullException( "player" );
            if (freePlayerIDs.Count > 0) {
                var newEntity = new VisibleEntity(VisibleEntity.HiddenPosition, freePlayerIDs.Pop(), player.Info.Rank);
                entities.Add(player, newEntity);
#if DEBUG_MOVEMENT
                Logger.Log( LogType.Debug, "AddEntity: {0} added {1} ({2})", Name, newEntity.Id, player.Name );
#endif
                Position pos = new Position(0, 0, 0);
                if (player.World != null) {
                    pos = player.WorldMap.Spawn;
                }
                if (Supports(CpeExt.ExtPlayerList2)) {
                    Send(Packet.MakeExtAddEntity2(newEntity.Id, player.Info.Rank.Color + player.Name,
                        (player.Info.skinName == "" ? player.Name : player.Info.skinName), pos, this));
                    Send(Packet.MakeTeleport(newEntity.Id, player.Position));
                } else {
                    Send(Packet.MakeAddEntity(newEntity.Id, player.Info.Rank.Color + player.Name,
                        player.WorldMap.Spawn));
                    Send(Packet.MakeTeleport(newEntity.Id, player.Position));
                }
                if (Supports(CpeExt.ChangeModel)) {
                    string addedModel = player.IsAFK ? player.AFKModel : player.Info.Mob;
                    if (Info.Rank.CanSee(player.Info.Rank) && (addedModel.CaselessEquals("air") || addedModel.CaselessEquals("0"))) {
                        addedModel = "Humanoid";
                    }
                    Send(Packet.MakeChangeModel((byte)newEntity.Id, addedModel));
                }
                return newEntity;
            } else {
                throw new InvalidOperationException("Player.AddEntity: Ran out of entity IDs.");
            }
        }        

        void HideEntity( [NotNull] VisibleEntity entity ) {
            if( entity == null ) throw new ArgumentNullException( "entity" );
#if DEBUG_MOVEMENT
            Logger.Log( LogType.Debug, "HideEntity: {0} no longer sees {1}", Name, entity.Id );
#endif
            entity.Hidden = true;
            entity.LastKnownPosition = VisibleEntity.HiddenPosition;
            SendNow( Packet.MakeTeleport( entity.Id, VisibleEntity.HiddenPosition ) );
        }


        void ShowEntity( [NotNull] VisibleEntity entity, Position newPos ) {
            if( entity == null ) throw new ArgumentNullException( "entity" );
#if DEBUG_MOVEMENT
            Logger.Log( LogType.Debug, "ShowEntity: {0} now sees {1}", Name, entity.Id );
#endif
            entity.Hidden = false;
            entity.LastKnownPosition = newPos;
            SendNow( Packet.MakeTeleport( entity.Id, newPos ) );
        }


        void ReAddEntity( [NotNull] VisibleEntity entity, [NotNull] Player player ) {
            if( entity == null ) throw new ArgumentNullException( "entity" );
            if( player == null ) throw new ArgumentNullException( "player" );
#if DEBUG_MOVEMENT
            Logger.Log( LogType.Debug, "ReAddEntity: {0} re-added {1} ({2})", Name, entity.Id, player.Name );
#endif
            SendNow( Packet.MakeRemoveEntity( entity.Id ) );
            if (Supports(CpeExt.ExtPlayerList2)) {
                SendNow(Packet.MakeExtAddEntity2(entity.Id, player.Info.Rank.Color + player.Name, 
                    (player.Info.skinName == "" ? player.Name : player.Info.skinName),
                    player.WorldMap.Spawn, this));
                SendNow(Packet.MakeTeleport(entity.Id, player.Position));
            } else {
                SendNow(Packet.MakeAddEntity(entity.Id, player.Info.Rank.Color + player.Name, player.WorldMap.Spawn));
                SendNow(Packet.MakeTeleport(entity.Id, player.Position));
            }

            if (Supports(CpeExt.ChangeModel)) {
                string readdedModel = player.IsAFK ? player.AFKModel : player.Info.Mob;
                if (Info.Rank.CanSee(player.Info.Rank) && (readdedModel.CaselessEquals("air") || readdedModel.CaselessEquals("0"))) {
                    readdedModel = "Humanoid";
                }
                SendNow(Packet.MakeChangeModel((byte)entity.Id, readdedModel));
            }
        }


        void RemoveEntity( [NotNull] Player player ) {
            if( player == null ) throw new ArgumentNullException( "player" );
#if DEBUG_MOVEMENT
            Logger.Log( LogType.Debug, "RemoveEntity: {0} removed {1} ({2})", Name, entities[player].Id, player.Name );
#endif
            SendNow( Packet.MakeRemoveEntity( entities[player].Id ) );
            freePlayerIDs.Push( entities[player].Id );
            entities.Remove( player );
        }


        void MoveEntity( [NotNull] VisibleEntity entity, Position newPos ) {
            if( entity == null ) throw new ArgumentNullException( "entity" );
            Position oldPos = entity.LastKnownPosition;

            // calculate difference between old and new positions
            Position delta = new Position {
                X = (short)( newPos.X - oldPos.X ),
                Y = (short)( newPos.Y - oldPos.Y ),
                Z = (short)( newPos.Z - oldPos.Z ),
                R = (byte)Math.Abs( newPos.R - oldPos.R ),
                L = (byte)Math.Abs( newPos.L - oldPos.L )
            };

            bool posChanged = ( delta.X != 0 ) || ( delta.Y != 0 ) || ( delta.Z != 0 );
            bool rotChanged = ( delta.R != 0 ) || ( delta.L != 0 );

            if( skipUpdates ) {
                int distSquared = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
                // movement optimization
                if( distSquared < SkipMovementThresholdSquared &&
                    ( delta.R * delta.R + delta.L * delta.L ) < SkipRotationThresholdSquared &&
                    !entity.SkippedLastMove ) {

                    entity.SkippedLastMove = true;
                    return;
                }
                entity.SkippedLastMove = false;
            }

            Packet packet;
            // create the movement packet
            if( partialUpdates && delta.FitsIntoMoveRotatePacket && fullUpdateCounter < FullPositionUpdateInterval ) {
                if( posChanged && rotChanged ) {
                    // incremental position + absolute rotation update
                    packet = Packet.MakeMoveRotate( entity.Id, new Position {
                        X = delta.X,
                        Y = delta.Y,
                        Z = delta.Z,
                        R = newPos.R,
                        L = newPos.L
                    } );

                } else if( posChanged ) {
                    // incremental position update
                    packet = Packet.MakeMove( entity.Id, delta );

                } else if( rotChanged ) {
                    // absolute rotation update
                    packet = Packet.MakeRotate( entity.Id, newPos );
                } else {
                    return;
                }

            } else {
                // full (absolute position + absolute rotation) update
                packet = Packet.MakeTeleport( entity.Id, newPos );
            }

            entity.LastKnownPosition = newPos;
            SendNow( packet );
        }

        public sealed class VisibleEntity {
            public static readonly Position HiddenPosition = new Position( 0, 0, short.MinValue );


            public VisibleEntity( Position newPos, sbyte newId, Rank newRank ) {
                Id = newId;
                LastKnownPosition = newPos;
                MarkedForRetention = true;
                Hidden = true;
                LastKnownRank = newRank;
            }


            public readonly sbyte Id;
            public Position LastKnownPosition;
            public Rank LastKnownRank;
            public bool Hidden;
            public bool MarkedForRetention;
            public bool SkippedLastMove;
        }

        internal Position lastValidPosition; // used in speedhack detection

        bool DetectMovementPacketSpam() {
            if( antiSpeedPacketLog.Count >= AntiSpeedMaxPacketCount ) {
                DateTime oldestTime = antiSpeedPacketLog.Dequeue();
                double spamTimer = DateTime.UtcNow.Subtract( oldestTime ).TotalSeconds;
                if( spamTimer < AntiSpeedMaxPacketInterval ) {
                    DenyMovement();
                    return true;
                }
            }
            antiSpeedPacketLog.Enqueue( DateTime.UtcNow );
            return false;
        }


        void DenyMovement() {
            SendNow( Packet.MakeSelfTeleport(new Position
                {
                    X = (short)(lastValidPosition.X),
                    Y = (short)(lastValidPosition.Y),
                    Z = (short)(lastValidPosition.Z + 22),
                    R = lastValidPosition.R,
                    L = lastValidPosition.L
                }));
            if( DateTime.UtcNow.Subtract( antiSpeedLastNotification ).Seconds > 1 ) {
                Message( "&WYou are not allowed to speedhack." );
                antiSpeedLastNotification = DateTime.UtcNow;
            }
        }

        #endregion


        #region Bandwidth Use Tweaks

        BandwidthUseMode bandwidthUseMode;
        int entityShowingThreshold, entityHidingThreshold;
        bool partialUpdates, skipUpdates;

        DateTime lastMovementUpdate;
        TimeSpan movementUpdateInterval;


        public BandwidthUseMode BandwidthUseMode {
            get { return bandwidthUseMode; }

            set {
                bandwidthUseMode = value;
                BandwidthUseMode actualValue = value;
                if( value == BandwidthUseMode.Default ) {
                    actualValue = ConfigKey.BandwidthUseMode.GetEnum<BandwidthUseMode>();
                }
                switch( actualValue ) {
                    case BandwidthUseMode.VeryLow:
                        entityShowingThreshold = ( 40 * 32 ) * ( 40 * 32 );
                        entityHidingThreshold = ( 42 * 32 ) * ( 42 * 32 );
                        partialUpdates = true;
                        skipUpdates = true;
                        movementUpdateInterval = TimeSpan.FromMilliseconds( 100 );
                        break;

                    case BandwidthUseMode.Low:
                        entityShowingThreshold = ( 50 * 32 ) * ( 50 * 32 );
                        entityHidingThreshold = ( 52 * 32 ) * ( 52 * 32 );
                        partialUpdates = true;
                        skipUpdates = true;
                        movementUpdateInterval = TimeSpan.FromMilliseconds( 50 );
                        break;

                    case BandwidthUseMode.Normal:
                        entityShowingThreshold = ( 68 * 32 ) * ( 68 * 32 );
                        entityHidingThreshold = ( 70 * 32 ) * ( 70 * 32 );
                        partialUpdates = true;
                        skipUpdates = false;
                        movementUpdateInterval = TimeSpan.FromMilliseconds( 50 );
                        break;

                    case BandwidthUseMode.High:
                        entityShowingThreshold = ( 128 * 32 ) * ( 128 * 32 );
                        entityHidingThreshold = ( 130 * 32 ) * ( 130 * 32 );
                        partialUpdates = true;
                        skipUpdates = false;
                        movementUpdateInterval = TimeSpan.FromMilliseconds( 50 );
                        break;

                    case BandwidthUseMode.VeryHigh:
                        entityShowingThreshold = int.MaxValue;
                        entityHidingThreshold = int.MaxValue;
                        partialUpdates = false;
                        skipUpdates = false;
                        movementUpdateInterval = TimeSpan.FromMilliseconds( 25 );
                        break;
                }
            }
        }

        #endregion


        #region Bandwidth Use Metering

        DateTime lastMeasurementDate = DateTime.UtcNow;
        int lastBytesSent, lastBytesReceived;


        /// <summary> Total bytes sent (to the client) this session. </summary>
        public int BytesSent { get; private set; }

        /// <summary> Total bytes received (from the client) this session. </summary>
        public int BytesReceived { get; private set; }

        /// <summary> Bytes sent (to the client) per second, averaged over the last several seconds. </summary>
        public double BytesSentRate { get; private set; }

        /// <summary> Bytes received (from the client) per second, averaged over the last several seconds. </summary>
        public double BytesReceivedRate { get; private set; }


        void MeasureBandwidthUseRates() {
            int sentDelta = BytesSent - lastBytesSent;
            int receivedDelta = BytesReceived - lastBytesReceived;
            TimeSpan timeDelta = DateTime.UtcNow.Subtract( lastMeasurementDate );
            BytesSentRate = sentDelta / timeDelta.TotalSeconds;
            BytesReceivedRate = receivedDelta / timeDelta.TotalSeconds;
            lastBytesSent = BytesSent;
            lastBytesReceived = BytesReceived;
            lastMeasurementDate = DateTime.UtcNow;
        }
        #endregion                      
    }
}