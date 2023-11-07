﻿#region

using System.Reflection;
using AGC_Management.Helpers;
using AGC_Management.LavaManager;
using AGC_Management.Services.DatabaseHandler;
using AGC_Management.Services.Logging;
using AGC_Management.Tasks;
using DisCatSharp;
using DisCatSharp.ApplicationCommands;
using DisCatSharp.ApplicationCommands.Attributes;
using DisCatSharp.ApplicationCommands.EventArgs;
using DisCatSharp.ApplicationCommands.Exceptions;
using DisCatSharp.CommandsNext;
using DisCatSharp.CommandsNext.Exceptions;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using DisCatSharp.EventArgs;
using DisCatSharp.Interactivity;
using DisCatSharp.Interactivity.Extensions;
using KawaiiAPI.NET;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

#endregion

namespace AGC_Management;

public class CurrentApplicationData
{
    public static DiscordClient Client { get; set; }
}

internal class Program : BaseCommandModule
{
    private static void Main(string[] args)
    {
        MainAsync().GetAwaiter().GetResult();
    }

    private static async Task MainAsync()
    {
        var logger = Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();
        
        logger.Information("Starting AGC Management Bot...");
        bool DebugMode;
        try
        {
            DebugMode = bool.Parse(BotConfig.GetConfig()["MainConfig"]["DebugMode"]);
        }
        catch
        {
            DebugMode = false;
        }

        string DcApiToken = "";
        try
        {
            DcApiToken = DebugMode
                ? BotConfig.GetConfig()["MainConfig"]["Discord_API_Token_DEB"]
                : BotConfig.GetConfig()["MainConfig"]["Discord_API_Token"];
        }
        catch
        {
            try
            {
                DcApiToken = BotConfig.GetConfig()["MainConfig"]["Discord_API_Token"];
            }
            catch
            {
                Console.WriteLine(
                    "Der Discord API Token konnte nicht geladen werden.");
                Console.WriteLine("Drücke eine beliebige Taste um das Programm zu beenden.");
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        var client = new KawaiiClient();


        var serviceProvider = new ServiceCollection()
            .AddLogging(lb => lb.AddSerilog())
            .AddSingleton(client)
            .AddSingleton<LoggingService>()
            .BuildServiceProvider();

        Log.Logger.Information("Environment Details\n\n" +
                               "Dotnet Version: {Version}\n" +
                               "OS & Version: {OSVersion}\n\n" +
                               "OS 64x: {Is64BitOperatingSystem}\n" +
                               "Process 64x: {Is64BitProcess}\n\n" +
                               "MachineName: {MachineName}\n" +
                               "UserName: {UserName}\n" +
                               "UserDomain: {UserDomainName}\n\n" +
                               "Current Directory: {CurrentDirectory}\n" +
                               "Commandline: {Commandline}\n",
            Environment.Version,
            Environment.OSVersion,
            Environment.Is64BitOperatingSystem,
            Environment.Is64BitProcess,
            Environment.MachineName,
            Environment.UserName,
            Environment.UserDomainName,
            Environment.CurrentDirectory,
            Environment.CommandLine);


        DatabaseService.OpenConnection();
        TicketDatabaseService.OpenConnection();
        var discord = new DiscordClient(new DiscordConfiguration
        {
            Token = DcApiToken,
            TokenType = TokenType.Bot,
            AutoReconnect = true,
            MinimumLogLevel = LogLevel.Debug,
            Intents = DiscordIntents.All,
            LogTimestampFormat = "MMM dd yyyy - HH:mm:ss tt",
            DeveloperUserId = GlobalProperties.BotOwnerId,
            Locale = "de",
            ServiceProvider = serviceProvider,
            MessageCacheSize = 10000
        });
        discord.RegisterEventHandlers(Assembly.GetExecutingAssembly());
        var commands = discord.UseCommandsNext(new CommandsNextConfiguration
        {
            PrefixResolver = GetPrefix,
            EnableDms = false,
            EnableMentionPrefix = true,
            IgnoreExtraArguments = true,
            EnableDefaultHelp = bool.Parse(BotConfig.GetConfig()["MainConfig"]["EnableBuiltInHelp"])
        });
        discord.ClientErrored += Discord_ClientErrored;
        discord.UseInteractivity(new InteractivityConfiguration
        {
            Timeout = TimeSpan.FromMinutes(2)
        });
        commands.RegisterCommands(Assembly.GetExecutingAssembly());
        var appCommands = discord.UseApplicationCommands(new ApplicationCommandsConfiguration
        {
            ServiceProvider = serviceProvider
        });
        appCommands.SlashCommandErrored += Discord_SlashCommandErrored;
        appCommands.RegisterGlobalCommands(Assembly.GetExecutingAssembly());

        commands.CommandErrored += Commands_CommandErrored;
        await discord.ConnectAsync();
        await LavalinkConnectionManager.ConnectAsync(discord);
        CurrentApplicationData.Client = discord;

        await StartTasks(discord);
        await Task.Delay(-1);
    }

    private static Task StartTasks(DiscordClient discord)
    {
        //// start Warn Expire Task
        ModerationSystemTasks MST = new();
        MST.StartRemovingWarnsPeriodically(discord);

        //// start TempVC Check Task
        TempVoiceTasks TVT = new();
        TVT.StartRemoveEmptyTempVoices(discord);
        return Task.CompletedTask;
    }


    private static async Task Discord_SlashCommandErrored(ApplicationCommandsExtension sender,
        SlashCommandErrorEventArgs e)
    {
        if (e.Exception is SlashExecutionChecksFailedException)
        {
            var ex = (SlashExecutionChecksFailedException)e.Exception;
            if (ex.FailedChecks.Any(x => x is ApplicationCommandRequireUserPermissionsAttribute))
            {
                var embed = EmbedGenerator.GetErrorEmbed(
                    "You don't have the required permissions to execute this command.");
                await e.Context.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder().AddEmbed(embed).AsEphemeral());
                e.Handled = true;
                return;
            }

            e.Handled = true;
        }
    }


    private static Task<int> GetPrefix(DiscordMessage message)
    {
        return Task.Run(() =>
        {
            string prefix;
            if (GlobalProperties.DebugMode)
                prefix = "!!!";
            else
                try
                {
                    prefix = BotConfig.GetConfig()["MainConfig"]["BotPrefix"];
                }
                catch
                {
                    prefix = "!!!"; //Fallback Config
                }

            int CommandStart = -1;
            CommandStart = message.GetStringPrefixLength(prefix);
            return CommandStart;
        });
    }


    private static Task Discord_ClientErrored(DiscordClient sender, ClientErrorEventArgs e)
    {
        sender.Logger.LogError($"Exception occured: {e.Exception.GetType()}: {e.Exception.Message}");
        sender.Logger.LogError($"Stacktrace: {e.Exception.GetType()}: {e.Exception.StackTrace}");
        return Task.CompletedTask;
    }

    private static async Task Commands_CommandErrored(CommandsNextExtension cn, CommandErrorEventArgs e)
    {
        if (e.Exception is ArgumentException)
        {
            DiscordEmbedBuilder eb;
            eb = new DiscordEmbedBuilder
            {
                Title = "Fehler | BadArgumentException",

                Color = new DiscordColor("#FF0000")
            };
            eb.WithDescription($"Fehlerhafte Argumente.\n" +
                               $"**Stelle sicher dass alle Argumente richtig angegeben sind!**");
            eb.WithFooter($"Fehler ausgelöst von {e.Context.User.UsernameWithDiscriminator}");
            await e.Context.RespondAsync(embed: eb, content: e.Context.User.Mention);
            return;
        }

        if (e.Exception is CommandNotFoundException)
        {
            return;
        }

        var embed = new DiscordEmbedBuilder
        {
            Title = "Fehler | CommandErrored",
            Color = new DiscordColor("#FF0000")
        };
        embed.WithDescription($"Es ist ein Fehler aufgetreten.\n" +
                              $"**Fehler: {e.Exception.Message}**");
        embed.WithFooter($"Fehler ausgelöst von {e.Context.User.UsernameWithDiscriminator}");
        await e.Context.RespondAsync(embed: embed, content: e.Context.User.Mention);
    }
}

public static class GlobalProperties
{
    // Server Staffrole ID
    public static ulong StaffRoleId { get; } = ulong.Parse(BotConfig.GetConfig()["ServerConfig"]["StaffRoleId"]);

    // Debug Mode
    public static bool DebugMode { get; } = ParseBoolean(BotConfig.GetConfig()["MainConfig"]["DebugMode"]);

    // Bot Owner ID
    public static ulong BotOwnerId { get; } = ulong.Parse(BotConfig.GetConfig()["MainConfig"]["BotOwnerId"]);

    private static bool ParseBoolean(string boolString)
    {
        if (bool.TryParse(boolString, out bool parsedBool))
            return parsedBool;
        return false;
    }
}