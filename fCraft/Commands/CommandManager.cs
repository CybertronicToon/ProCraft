﻿// Part of fCraft | Copyright 2009-2015 Matvei Stefarov <me@matvei.org> | BSD-3 | See LICENSE.txt //Copyright (c) 2011-2013 Jon Baker, Glenn Marien and Lao Tszy <Jonty800@gmail.com> //Copyright (c) <2012-2014> <LeChosenOne, DingusBungus> | ProCraft Copyright 2014-2019 Joseph Beauvais <123DMWM@gmail.com>
using System;
using System.Collections.Generic;
using System.Linq;
using fCraft.Events;
using JetBrains.Annotations;

namespace fCraft
{
    /// <summary> Static class that allows registration and parsing of all text commands. </summary>
    public static class CommandManager
    {
        static readonly SortedList<string, string> Aliases = new SortedList<string, string>();
        static readonly SortedList<string, CommandDescriptor> Commands = new SortedList<string, CommandDescriptor>();

        /// <summary> Set of reserved command names (ok, nvm, and client). </summary>
        public static readonly string[] ReservedCommandNames = { "ok", "nvm", "client" };

        // Sets up all the command hooks
        internal static void Init()
        {
            ModerationCommands.Init();
            BuildingCommands.Init();
            InfoCommands.Init();
            WorldCommands.Init();
            ZoneCommands.Init();
            MaintenanceCommands.Init();
            ChatCommands.Init();
            CpeCommands.Init();
            Logger.Log(LogType.Debug,
                        "CommandManager: {0} commands registered ({1} hidden, {2} aliases)",
                        Commands.Count,
                        GetCommands(true).Length,
                        Aliases.Count);
        }


        /// <summary> Gets a list of all commands (including hidden ones). </summary>
        public static CommandDescriptor[] GetCommands()
        {
            return Commands.Values.ToArray();
        }


        /// <summary> Gets a list of ONLY hidden or non-hidden commands, not both. </summary>
        public static CommandDescriptor[] GetCommands(bool hidden)
        {
            return Commands.Values
                           .Where(cmd => (cmd.IsHidden == hidden))
                           .ToArray();
        }


        /// <summary> Gets a list of commands available to a specified rank. </summary>
        public static CommandDescriptor[] GetCommands([NotNull] Rank rank, bool includeHidden)
        {
            if (rank == null) throw new ArgumentNullException("rank");
            return Commands.Values
                           .Where(cmd => (!cmd.IsHidden || includeHidden) &&
                                          cmd.CanBeCalledBy(rank))
                           .ToArray();
        }


        /// <summary> Gets a list of commands in a specified category.
        /// Note that commands may belong to more than one category. </summary>
        public static CommandDescriptor[] GetCommands(CommandCategory category, bool includeHidden)
        {
            return Commands.Values
                           .Where(cmd => (includeHidden || !cmd.IsHidden) &&
                                          (cmd.Category & category) == category)
                           .ToArray();
        }


        /// <summary> Registers a custom command with fCraft, to be made available for players to call.
        /// Raises CommandManager.CommandRegistering/CommandRegistered events. 
        /// CommandRegistering event may silently cancel the registration. </summary>
        /// <param name="descriptor"> Command descriptor to register. </param>
        /// <exception cref="ArgumentNullException"> descriptor is null. </exception>
        /// <exception cref="CommandRegistrationException"> Command descriptor could not be registered.
        /// Check exception message for details. Possible reasons include:
        /// No category/name/handler was set; a command with the same name has already been registered; command name is reserved;
        /// or one of the aliases of given command matches the name of another registered command. </exception>
        public static void RegisterCustomCommand([NotNull] CommandDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException("descriptor");
            descriptor.IsCustom = true;
            RegisterCommand(descriptor);
        }


        internal static void RegisterCommand([NotNull] CommandDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException("descriptor");

#if DEBUG
            if( descriptor.Category == CommandCategory.None && !descriptor.IsCustom ) {
                throw new CommandRegistrationException( descriptor, "Standard commands must have a category set." );
            }
#endif

            if (!IsValidCommandName(descriptor.Name))
            {
                throw new CommandRegistrationException(descriptor,
                                                        "All commands need a name, between 1 and 16 alphanumeric characters long.");
            }

            string normalizedName = descriptor.Name.ToLower();

            if (Commands.ContainsKey(normalizedName))
            {
                throw new CommandRegistrationException(descriptor,
                                                        "A command with the name \"{0}\" is already registered.",
                                                        descriptor.Name);
            }

            if (ReservedCommandNames.Contains(normalizedName))
            {
                throw new CommandRegistrationException(descriptor, "The command name is reserved.");
            }

            if (descriptor.Handler == null)
            {
                throw new CommandRegistrationException(descriptor,
                                                        "All command descriptors are required to provide a handler callback.");
            }

            if (descriptor.Aliases != null)
            {
                if (descriptor.Aliases.Any(alias => Commands.ContainsKey(alias.ToLowerInvariant())))
                {
                    throw new CommandRegistrationException(descriptor,
                                                            "One of the aliases for \"{0}\" is using the name of an already-defined command.",
                                                            descriptor.Name);
                }
            }

            if (!Char.IsUpper(descriptor.Name[0]))
            {
                descriptor.Name = descriptor.Name.UppercaseFirst();
            }

            if (descriptor.Usage == null)
            {
                descriptor.Usage = "/" + descriptor.Name;
            }

            if (RaiseCommandRegisteringEvent(descriptor)) return;

            if (Aliases.ContainsKey(normalizedName))
            {
                Logger.Log(LogType.Warning,
                            "CommandManager.RegisterCommand: \"{0}\" was defined as an alias for \"{1}\", " +
                            "but has now been replaced by a different command of the same name.",
                            descriptor.Name, Aliases[descriptor.Name]);
                Aliases.Remove(normalizedName);
            }

            if (descriptor.Aliases != null)
            {
                foreach (string alias in descriptor.Aliases)
                {
                    string normalizedAlias = alias.ToLowerInvariant();
                    if (ReservedCommandNames.Contains(normalizedAlias) &&
                        !(descriptor.Name == "Cancel" && alias == "Nvm"))
                    { // special case for cancel/nvm aliases
                        Logger.Log(LogType.Warning,
                                    "CommandManager.RegisterCommand: Alias \"{0}\" for \"{1}\" ignored (reserved name).",
                                    alias, descriptor.Name);
                    }
                    else if (Aliases.ContainsKey(normalizedAlias))
                    {
                        Logger.Log(LogType.Warning,
                                    "CommandManager.RegisterCommand: \"{0}\" was defined as an alias for \"{1}\", " +
                                    "but has been overridden to resolve to \"{2}\" instead.",
                                    alias, Aliases[normalizedAlias], descriptor.Name);
                    }
                    else
                    {
                        Aliases.Add(normalizedAlias, normalizedName);
                    }
                }
            }

            Commands.Add(normalizedName, descriptor);

            RaiseCommandRegisteredEvent(descriptor);
        }


        /// <summary> Finds an instance of CommandDescriptor for a given command.
        /// Case-insensitive, but no autocompletion. </summary>
        /// <param name="commandName"> Command to find. </param>
        /// <param name="alsoCheckAliases"> Whether to check command aliases. </param>
        /// <returns> Relevant CommandDescriptor object if found, null if not found. </returns>
        /// <exception cref="ArgumentNullException"> commandName is null. </exception>
        [CanBeNull]
        public static CommandDescriptor GetDescriptor([NotNull] string commandName, bool alsoCheckAliases)
        {
            if (commandName == null) throw new ArgumentNullException("commandName");
            commandName = commandName.ToLower();
            if (Commands.ContainsKey(commandName))
            {
                return Commands[commandName];
            }
            else if (alsoCheckAliases && Aliases.ContainsKey(commandName))
            {
                return Commands[Aliases[commandName]];
            }
            else
            {
                return null;
            }
        }


        /// <summary> Parses and calls a specified command. </summary>
        /// <param name="player"> Player who issued the command. </param>
        /// <param name="cmd"> Command to be parsed and executed. </param>
        /// <param name="fromConsole"> Whether this command is being called from a non-player (e.g. Console). </param>
        /// <returns> True if the command was called, false if something prevented it from being called. </returns>
        /// <exception cref="ArgumentNullException"> player or cmd is null. </exception>
        public static bool ParseCommand([NotNull] Player player, [NotNull] CommandReader cmd, bool fromConsole)
        {
            if (player == null) throw new ArgumentNullException("player");
            if (cmd == null) throw new ArgumentNullException("cmd");
            CommandDescriptor descriptor = cmd.Descriptor;

            if (descriptor == null)
            {
                if (CommandManager.ParseUnknownCommand(player, cmd))
                    return true;
                player.Message("Unknown command \"{0}\". See &H/Commands", cmd.Name);
                return false;
            }
            if (!descriptor.IsConsoleSafe && fromConsole)
            {
                player.Message("You cannot use this command from console.");
                return false;
            }
            
            if (descriptor.Permissions != null)
            {
                if (!descriptor.CanBeCalledBy(player.Info.Rank))
                {
                    player.MessageNoAccess(descriptor);
                    return false;
                }
                
                if (descriptor.MinRank != RankManager.LowestRank && !player.Info.ClassicubeVerified) {
                    player.Message("As you had an older minecraft.net account, you must have an admin verify your " +
                                   "new classicube.net account actually is you with /verify before you can use non-guest commands.");
                    return false;
                }
            }
            
            if (descriptor.Call(player, cmd, true))
            {
                return true;
            }
            else
            {
                player.Message("Command was cancelled.");
                return false;
            }
        }
        /// <summary> Parses an unknown command as a /Join [command] command. </summary>
        /// <param name="player"> Player who issued the command. </param>
        /// <param name="cmd"> Command to be parsed as a worldname. </param>
        /// <returns> True if the command was a world and the user was able to join it, false if world doesn't exist, or user is unable to join the world. </returns>
        public static bool ParseUnknownCommand(Player player, CommandReader cmd) {
            //joinworld or tp to player
            if (cmd.RawMessage.IndexOf(' ') == -1 && player != Player.Console) {
                string cmdString = cmd.RawMessage.Substring(1);
                bool wasWorldTP = false;
                if (cmdString == "-") {
                    if (player.LastUsedWorldName != null) {
                        cmdString = player.LastUsedWorldName;
                    } else {
                        return false;
                    }
                }
                World[] worlds = WorldManager.FindWorlds(player, cmdString);

                if (worlds.Length == 1) {
                    World world = worlds[0];
                    if (world.Name.StartsWith("PW_")) {
                        return false;
                    }
                    player.LastUsedWorldName = world.Name;
                    switch (world.AccessSecurity.CheckDetailed(player.Info)) {
                        case SecurityCheckResult.Allowed:
                        case SecurityCheckResult.WhiteListed:
                            if (world.IsFull) {
                                break;
                            }
                            if (cmd.IsConfirmed) {
                                if (player.JoinWorldNow(world, true, WorldChangeReason.ManualJoin)) {
                                    wasWorldTP = true;
                                }
                                break;
                            }
                            if (player.World.Name.CaselessEquals("tutorial") && !player.Info.HasRTR) {
                                player.Confirm(cmd,
                                    "&SYou are choosing to skip the rules, if you continue you will spawn here the next time you log in.");
                                return true;
                            }
                            player.StopSpectating();
                            if (player.JoinWorldNow(world, true, WorldChangeReason.ManualJoin)) {
                                wasWorldTP = true;
                                break;
                            }
                            break;
                        case SecurityCheckResult.BlackListed:
                            break;
                        case SecurityCheckResult.RankTooLow:
                            break;
                    }
                    if (wasWorldTP) {
                        player.Message("&H{0}&S is not a command, but it part of a world name, so you have been teleported to {1}&S instead", cmd.RawMessage, world.ClassyName);
                        player.SendToSpectators(cmd.RawMessage + " -> /Join {0}", world.Name);
                        Logger.Log(LogType.UserCommand, "{0}: /Join {1}", player.Name, world.Name);
                        return true;
                    }
                }
            }
            return false;
        }


        /// <summary> Checks whether a command name is acceptable.
        /// Constraints are similar to Player.IsValidPlayerName, except for length.
        /// Command names must bet between 1 and 16 characters long. </summary>
        /// <param name="name"> Command name to check. </param>
        /// <returns> True if the given name is valid; otherwise false. </returns>
        /// <exception cref="ArgumentNullException"> name is null. </exception>
        public static bool IsValidCommandName([NotNull] string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (name.Length == 0 || name.Length > 16) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if ((ch < '0' && ch != '.') || (ch > '9' && ch < 'A') || (ch > 'Z' && ch < '_') ||
                    (ch > '_' && ch < 'a') || ch > 'z')
                {
                    return false;
                }
            }
            return true;
        }


        #region Events

        /// <summary> Occurs when a command is being registered (cancelable). </summary>
        public static event EventHandler<CommandRegisteringEventArgs> CommandRegistering;

        /// <summary> Occurs when a command has been registered. </summary>
        public static event EventHandler<CommandRegisteredEventArgs> CommandRegistered;

        /// <summary> Occurs when a command is being called by a player or the console (cancelable). </summary>
        public static event EventHandler<CommandCallingEventArgs> CommandCalling;

        /// <summary> Occurs when the command has been called by a player or the console. </summary>
        public static event EventHandler<CommandCalledEventArgs> CommandCalled;


        static bool RaiseCommandRegisteringEvent(CommandDescriptor descriptor)
        {
            var h = CommandRegistering;
            if (h == null) return false;
            var e = new CommandRegisteringEventArgs(descriptor);
            h(null, e);
            return e.Cancel;
        }


        static void RaiseCommandRegisteredEvent(CommandDescriptor descriptor)
        {
            var h = CommandRegistered;
            if (h != null) h(null, new CommandRegisteredEventArgs(descriptor));
        }


        internal static bool RaiseCommandCallingEvent(CommandReader cmd, CommandDescriptor descriptor, Player player)
        {
            var h = CommandCalling;
            if (h == null) return false;
            var e = new CommandCallingEventArgs(cmd, descriptor, player);
            h(null, e);
            return e.Cancel;
        }


        internal static void RaiseCommandCalledEvent(CommandReader cmd, CommandDescriptor descriptor, Player player)
        {
            var h = CommandCalled;
            if (h != null) CommandCalled(null, new CommandCalledEventArgs(cmd, descriptor, player));
        }

        #endregion
    }


    /// <summary> Exception that is thrown when an attempt to register a command has failed. </summary>
    public sealed class CommandRegistrationException : Exception
    {
        internal CommandRegistrationException([NotNull] CommandDescriptor descriptor, [NotNull] string message)
            : base(message)
        {
            if (descriptor == null) throw new ArgumentNullException("descriptor");
            Descriptor = descriptor;
        }


        [StringFormatMethod("message")]
        internal CommandRegistrationException([NotNull] CommandDescriptor descriptor,
                                              [NotNull] string message, [NotNull] params object[] args)
            : base(String.Format(message, args))
        {
            if (descriptor == null) throw new ArgumentNullException("descriptor");
            if (args == null) throw new ArgumentNullException("args");
            Descriptor = descriptor;
        }


        /// <summary> Descriptor for the command that could not be registered. </summary>
        [NotNull]
        public CommandDescriptor Descriptor { get; private set; }
    }
}


namespace fCraft.Events
{
    /// <summary> Provides data for CommandManager.CommandRegistered event. Immutable. </summary>
    public class CommandRegisteredEventArgs : EventArgs
    {
        internal CommandRegisteredEventArgs(CommandDescriptor descriptor)
        {
            Descriptor = descriptor;
        }

        public CommandDescriptor Descriptor { get; private set; }
    }


    /// <summary> Provides data for CommandManager.CommandRegistering event. Cancelable. </summary>
    public sealed class CommandRegisteringEventArgs : CommandRegisteredEventArgs, ICancelableEvent
    {
        internal CommandRegisteringEventArgs(CommandDescriptor descriptor)
            : base(descriptor)
        {
        }

        public bool Cancel { get; set; }
    }


    /// <summary> Provides data for CommandManager.CommandCalled event. Immutable. </summary>
    public class CommandCalledEventArgs : EventArgs, IPlayerEvent
    {
        internal CommandCalledEventArgs(CommandReader command, CommandDescriptor descriptor, Player player)
        {
            Command = command;
            Descriptor = descriptor;
            Player = player;
        }

        public CommandReader Command { get; private set; }
        public CommandDescriptor Descriptor { get; private set; }
        public Player Player { get; private set; }
    }


    /// <summary> Provides data for CommandManager.CommandCalling event. Cancelable. </summary>
    public sealed class CommandCallingEventArgs : CommandCalledEventArgs, ICancelableEvent
    {
        internal CommandCallingEventArgs(CommandReader command, CommandDescriptor descriptor, Player player) :
            base(command, descriptor, player)
        {
        }

        public bool Cancel { get; set; }
    }
}