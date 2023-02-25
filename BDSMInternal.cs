using System.Collections.Immutable;
using System.Diagnostics;

using FluentFTP.Exceptions;

using NLog;

using Spectre.Console;

using static BDSM.Lib.Configuration;
using static BDSM.Lib.Exceptions;
using static BDSM.Lib.BetterRepackRepositoryDefinitions;

namespace BDSM;

public static partial class BDSM
{
	// https://spectreconsole.net/appendix/colors
	public const  string HighlightColor   = "orchid2";
	public const  string ModpackNameColor = "springgreen1";
	public const  string FileListingColor = "skyblue1";
	public const  string SuccessColor     = "green1";
	public const  string WarningColor     = "yellow3_1";
	public const  string CancelColor      = "gold3_1";
	public const  string ErrorColor       = "red1";
	public const  string ErrorColorAlt    = "red3";
	public const  string DeleteColor      = "orangered1";
	public static string Colorize<T1>(this T1 plain, Color  color)     => $"[{color.ToMarkup()}]{plain}[/]";
	public static string Colorize<T1>(this T1 plain, string colorName) => $"[{colorName}]{plain}[/]";

	internal readonly record struct DownloadCategories
	{
		internal required IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> QueuedDownloads
		{
			get;
			init;
		}

		internal required IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> CanceledDownloads
		{
			get;
			init;
		}

		internal required IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> CompletedDownloads
		{
			get;
			init;
		}

		internal required IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> FailedDownloads
		{
			get;
			init;
		}

		internal IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> UnfinishedDownloads =>
			QueuedDownloads.Concat(CanceledDownloads);
	}

	internal class BDSMInternalFaultException : Exception
	{
		private const string BugReportSuffix =
			" Please file a bug report and provide this information. https://github.com/RobotsOnDrugs/BDSM/issues\r\n";

		internal BDSMInternalFaultException() { }
		internal BDSMInternalFaultException(string? message) : base(message + BugReportSuffix) { }

		internal BDSMInternalFaultException(string? message, bool includeBugReportLink) : base(message +
			(includeBugReportLink ? "" : BugReportSuffix))
		{
		}

		internal BDSMInternalFaultException(string? message, Exception? innerException) : base(message, innerException)
		{
		}
	}
#if DEBUG
	private static void RaiseInternalFault(ILogger ilog, string message)
	{
		ilog.Debug(message);
		Debugger.Break();
	}
#else
	private static void RaiseInternalFault(ILogger logger, string message)
	{
		BDSMInternalFaultException int_ex = new(message);
		LoggingConfiguration.LogExceptionAndDisplay(logger, int_ex);
		throw int_ex;
	}
#endif
	private static List<Task> ProcessTasks(List<Task> tasks, CancellationTokenSource cts)
	{
		bool userCanceled = false;

		void CatchCtrlC(object sender, ConsoleCancelEventArgs args)
		{
			cts.Cancel(false);
			userCanceled = true;
			args.Cancel  = true;
			LogManager.Shutdown();
		}

		Console.CancelKeyPress += CatchCtrlC!;
		List<Task>               finishedTasks = new(tasks.Count);
		List<AggregateException> exceptions    = new();
		do
		{
			int  completedTaskIdx = Task.WaitAny(tasks.ToArray());
			Task completedTask    = tasks[completedTaskIdx];
			switch (completedTask.Status)
			{
				case TaskStatus.RanToCompletion:
					break;
				case TaskStatus.Canceled:
					Logger.Log(LogLevel.Info, completedTask.Exception, "A task was canceled.");
					break;
				case TaskStatus.Faulted:
					AggregateException taskex = completedTask.Exception!;
					exceptions.Add(taskex);
					Exception innerex = taskex.InnerException!;
					Logger.Log(LogLevel.Warn, taskex.Flatten().InnerException, "A task was faulted.");
					if (innerex is FtpCommandException fcex && fcex.CompletionCode is "421")
						break;
					if (innerex is not FtpOperationException)
						cts.Cancel();
					break;
				case TaskStatus.Created:
					Logger.Log(LogLevel.Debug, "Task created.");
					break;
				case TaskStatus.WaitingForActivation:
					Logger.Log(LogLevel.Debug, "Task waiting for activation.");
					break;
				case TaskStatus.WaitingToRun:
					Logger.Log(LogLevel.Debug, "Task waiting to run.");
					break;
				case TaskStatus.Running:
					Logger.Log(LogLevel.Debug, "Task running.");
					break;
				case TaskStatus.WaitingForChildrenToComplete:
					Logger.Log(LogLevel.Debug, "Task waiting for children to complete.");
					break;
				default:
					exceptions.Add(new AggregateException(
						new BDSMInternalFaultException("Internal error while processing task exceptions.")));
					break;
			}

			finishedTasks.Add(completedTask);
			tasks.RemoveAt(completedTaskIdx);
		} while (tasks.Count != 0);

		Console.CancelKeyPress -= CatchCtrlC!;
		return !userCanceled ? finishedTasks : throw new OperationCanceledException();
	}

	private static FullUserConfiguration GenerateNewUserConfig()
	{
		bool isHs2 = false;
		while (true)
		{
			string gamepath =
				AnsiConsole.Ask<string>("Where is your game located? (e.g. " + "/games/hs2".Colorize(FileListingColor) +
				                        ")");

			isHs2 = GamePathIsHS2(gamepath);
			if (isHs2 is true)
				AnsiConsole.MarkupLine(
					$"- Looks like {(isHs2 ? "Honey Select 2" : "AI-Shoujo")} -".Colorize(SuccessColor));
			else
			{
				isHs2 = false;
				AnsiConsole.MarkupLine($"{gamepath} doesn't appear to be a valid game directory.");
				if (AnsiConsole.Confirm("Enter a new game folder?"))
					continue;
				throw new OperationCanceledException("User canceled user configuration creation.");
			}

			bool studio     = AnsiConsole.Confirm("Download studio mods?",       DefaultModpacksSimpleHS2.Studio);
			bool studioMaps = AnsiConsole.Confirm("Download extra studio maps?", studio);
			bool hs2Maps = isHs2 &&
			               AnsiConsole.Confirm("Download extra main game maps?", DefaultModpacksSimpleHS2.StudioMaps);
			bool bleedingedge = AnsiConsole.Confirm("Download bleeding edge mods? (Warning: these can break things)",
				DefaultModpacksSimpleHS2.BleedingEdge);
			bool userdata = AnsiConsole.Confirm("Download modpack user data such as character and clothing cards?",
				DefaultModpacksSimpleHS2.Userdata);
			bool promptToContinue = AnsiConsole.Confirm("Pause between steps to review information? (recommended)");
			SimpleUserConfiguration.Modpacks desiredModpacks = DefaultModpacksSimpleHS2 with
			{
				Studio = studio,
				StudioMaps = studioMaps,
				HS2Maps = hs2Maps,
				BleedingEdge = bleedingedge,
				Userdata = userdata
			};
			ImmutableHashSet<string> desiredModpackNames = GetDesiredModpackNames(isHs2, desiredModpacks);
			RepoConnectionInfo       connectionInfo      = DefaultConnectionInfo;
			FullUserConfiguration userconfig = new()
			{
				GamePath       = gamepath,
				ConnectionInfo = connectionInfo,
				BasePathMappings =
					ModpackNamesToPathMappings(desiredModpackNames, gamepath, connectionInfo.RootPath),
				PromptToContinue = promptToContinue
			};
			SerializeUserConfiguration(FullUserConfigurationToSimple(userconfig));
			AnsiConsole.MarkupLine("- New user configuration successfully created! -".Colorize(SuccessColor));
			return userconfig;
		}
	}
}