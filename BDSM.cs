using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;

using BDSM.Lib;

using FluentFTP;

using NLog;

using ShellProgressBar;

using Spectre.Console;

using YamlDotNet.Core;
using YamlDotNet.Serialization;

using static BDSM.Lib.FtpFunctions;
using static BDSM.DownloadProgress;
using static BDSM.LoggingConfiguration;
using static BDSM.Lib.Configuration;
using static BDSM.Lib.Utility;

namespace BDSM;

public static partial class BDSM
{
	public const string Version = "0.3.11";

	// [LibraryImport("kernel32.dll", SetLastError = true)]
	// [return: MarshalAs(UnmanagedType.Bool)]
	// private static partial bool SetConsoleOutputCP(uint wCodePageID);
	// [LibraryImport("kernel32.dll", SetLastError = true)]
	// [return: MarshalAs(UnmanagedType.Bool)]
	// private static partial bool SetConsoleCP(uint wCodePageID);
	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

	internal static FullUserConfiguration UserConfig;

	private static void CtrlCHandler(object sender, ConsoleCancelEventArgs args)
	{
		Console.WriteLine("");
		LogWithMarkup(Logger, LogLevel.Fatal, "Update aborted, shutting down.");
		LogManager.Flush();
		LogManager.Shutdown();
		args.Cancel = false;
		Environment.Exit(1);
	}

	private static void DeletionCtrlCHandler(object sender, ConsoleCancelEventArgs args)
	{
		Console.WriteLine("");
		LogWithMarkup(Logger, LogLevel.Fatal, "Update aborted, shutting down.");
		LogManager.Flush();
		LogManager.Shutdown();
		throw new BDSMInternalFaultException("File deletion was aborted, likely due to an error in scanning.");
	}

	public static async Task<int> Main()
	{
		// _ = SetConsoleOutputCP(65001);
		// _ = SetConsoleCP(65001);
		Console.CancelKeyPress += CtrlCHandler!;
#if DEBUG
		LogManager.Configuration = LoadCustomConfiguration(out bool isCustomLogger, LogLevel.Debug);
#else
		LogManager.Configuration = LoadCustomConfiguration(out bool is_custom_logger, LogLevel.Info);
#endif
		Logger.Info($"== Begin BDSM {Version} log ==");
		Logger.Debug($"== Begin BDSM {Version} debug log ==");
		Logger.Info("Logger initialized.");

		if (isCustomLogger)
			LogWithMarkup(Logger, LogLevel.Info, "Custom logging configuration loaded successfully.", SuccessColor);
		InitalizeLibraryLoggers(Logger);
		FtpFunctionOptions ftpOptions = new() { BufferSize = 65536 };

		const string currentConfigVersion = "0.3.2";
		string       userConfigVersion    = currentConfigVersion;
		try { UserConfig = GetUserConfiguration(out userConfigVersion); }
		catch (UserConfigurationException ex)
		{
			switch (ex.InnerException)
			{
				case FileNotFoundException:
					if (AnsiConsole.Confirm("No configuration file found. Create one now?"))
						try
						{
							UserConfig = GenerateNewUserConfig();
							break;
						}
						catch (OperationCanceledException) { }

					LogWithMarkup(Logger, LogLevel.Fatal,
						"No configuration file was found and the creation of a new one was canceled.");
					return 1;
				case TypeInitializationException or YamlException:
					UserConfig        = await GetOldUserConfigurationAsync();
					userConfigVersion = "0.1";
					break;
				case null:
					LogWithMarkup(Logger, LogLevel.Fatal, ex.Message.EscapeMarkup());
					PromptBeforeExit();
					return 1;
				default:
					throw;
			}
		}

		if (GamePathIsHS2(UserConfig.GamePath) is false && GamePathIsAIS(UserConfig.GamePath) is false)
		{
			LogWithMarkup(Logger, LogLevel.Error, $"Your game path {UserConfig.GamePath.EscapeMarkup()} is not valid.");
			return 1;
		}

		if (userConfigVersion != currentConfigVersion)
		{
			try
			{
				SerializeUserConfiguration(FullUserConfigurationToSimple(UserConfig));
				LogWithMarkup(Logger, LogLevel.Info,
					$"Configuration file was updated from {userConfigVersion} to the {currentConfigVersion} format.",
					SuccessColor);
			}
			catch (Exception serialEx)
			{
				Logger.Warn(serialEx);
				AnsiConsole.MarkupLine(
					"Your configuration was updated, but the new configuration file could not be written. See BDSM.log for details."
						.Colorize(CancelColor));
				PromptUserToContinue();
			}

			AnsiConsole.MarkupLine(
				"See [link]https://github.com/RobotsOnDrugs/BDSM/wiki/User-Configuration[/] for more details on the new format.");
		}

		switch (userConfigVersion)
		{
			case currentConfigVersion:
				break;
			case "0.3":
				SimpleUserConfiguration simpleConfig = FullUserConfigurationToSimple(UserConfig);
				string studioModDownloadState =
					simpleConfig.OptionalModpacks.Studio ? "both packs" : "neither pack";
				LogWithMarkup(Logger, LogLevel.Warn,
					"Notice: Downloading extra studio maps was turned on by default in 0.3, but is now turned on by default only if studio mods are also being downloaded.");
				LogWithMarkup(Logger, LogLevel.Warn,
					"With your configuration, {studio_mod_download_state} will be downloaded. If you wish to change this, you may exit now and edit UserConfiguration.yaml to your liking.");
				PromptUserToContinue();
				break;
			case "0.1":
				LogWithMarkup(Logger, LogLevel.Warn,
					"Notice: As of 0.3, server connection info and server path mappings and sync behavior are no longer in the user configuration. If you have a good use case for customizing these, file a feature request on GitHub.");
				break;
			default:
				throw new BDSMInternalFaultException("Detection of user configuration version failed.");
		}

		ImmutableHashSet<PathMapping>             baseDirectoriesToScan;
		ConcurrentBag<PathMapping>                directoriesToScan = new();
		ConcurrentDictionary<string, PathMapping> filesOnServer     = new();
		ConcurrentBag<FileDownload>               filesToDownload   = new();
		ConcurrentBag<FileInfo>                   filesToDelete     = new();

		const string skipScanConfigFilename = "SkipScan.yaml";
		bool         skipScan               = false;
#if DEBUG
		SkipScanConfiguration skipConfig = File.Exists(skipScanConfigFilename)
			? new Deserializer().Deserialize<SkipScanConfiguration>(ReadConfigAndDispose(skipScanConfigFilename))
			: new SkipScanConfiguration { SkipScan = false, FileMappings = Array.Empty<string>() };
		skipScan = skipConfig.SkipScan;

		if (skipScan)
		{
			Logger.Info("SkipScan is enabled.");
			using FtpClient scanner = SetupFtpClient(UserConfig.ConnectionInfo);
			scanner.Connect();
			ConcurrentBag<PathMapping> skipscanPm = GetPathMappingsFromSkipScanConfig(skipConfig, UserConfig);
			foreach (PathMapping pathmap in skipscanPm)
			{
				FtpListItem? dlFileInfo = scanner.GetObjectInfo(pathmap.RemoteFullPath);
				if (dlFileInfo is not null)
					filesToDownload.Add(PathMappingToFileDownload(pathmap with { FileSize = dlFileInfo.Size }));
				else
					LogWithMarkup(Logger, LogLevel.Fatal,
						$"Couldn't get file info for {pathmap.RemoteFullPath.EscapeMarkup()}.");
			}

			scanner.Dispose();
		}
#endif
		try
		{
			baseDirectoriesToScan = skipScan ? ImmutableHashSet<PathMapping>.Empty : UserConfig.BasePathMappings;
		}
		catch (FormatException)
		{
			LogWithMarkup(Logger, LogLevel.Error,
				"Your configuration file is malformed. Please reference the example and read the documentation.");
			PromptBeforeExit();
			return 1;
		}

		foreach (PathMapping mapping in baseDirectoriesToScan)
			directoriesToScan.Add(mapping);

		Logger.Debug($"Using {UserConfig.ConnectionInfo.Address}");
		Stopwatch opTimer        = new();
		bool      noneSuccessful = true;
		bool      allFaulted     = true;
		Console.CancelKeyPress -= CtrlCHandler!;
		bool successfulScan = AnsiConsole.Status()
			.AutoRefresh(true)
			.SpinnerStyle(new Style(Color.Cyan1))
			.Spinner(Spinner.Known.BouncingBar)
			.Start("Scanning the server.", _ =>
			{
				opTimer.Start();
				List<Task>                    scanTasks         = new();
				List<Task>                    finishedScanTasks = new();
				using CancellationTokenSource scanCts           = new();
				CancellationToken             scanCt            = scanCts.Token;
				for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
					scanTasks.Add(Task.Run(
						() => GetFilesOnServer(ref directoriesToScan, ref filesOnServer, UserConfig.ConnectionInfo,
							scanCt), scanCt));
				try
				{
					finishedScanTasks = ProcessTasks(scanTasks, scanCts);
					List<Exception> scanExceptions = new();
					foreach (Task finishedScanTask in finishedScanTasks)
						switch (finishedScanTask.Status)
						{
							case TaskStatus.Faulted:
								scanExceptions.Add(finishedScanTask.Exception!);
								break;
							case TaskStatus.RanToCompletion:
								allFaulted     = false;
								noneSuccessful = false;
								break;
							case TaskStatus.Canceled:
								allFaulted = false;
								LogWithMarkup(Logger, LogLevel.Fatal, "Scanning was canceled.", CancelColor);
								break;
							default:
								allFaulted = false;
								break;
						}

					if (allFaulted)
						throw new AggregateException(scanExceptions);
				}
				catch (OperationCanceledException)
				{
					LogWithMarkup(Logger, LogLevel.Fatal, "Scanning was canceled.", CancelColor);
					return false;
				}
				catch (AggregateException aex)
				{
					if (allFaulted || noneSuccessful)
					{
						LogWithMarkup(Logger, LogLevel.Fatal,
							"Could not scan the server. Failed scan tasks had the following errors:");
						foreach (Exception innerEx in aex.Flatten().InnerExceptions)
						{
							AnsiConsole.MarkupLine(innerEx.Message.Colorize(ErrorColorAlt));
							Logger.Warn(innerEx);
						}

						AnsiConsole.MarkupLine("See the log for full error details.".Colorize(ErrorColor));
						return false;
					}
				}

				return true;
			});
		if (!successfulScan)
		{
			if (UserConfig.PromptToContinue)
				PromptBeforeExit();
			return 1;
		}

		Console.CancelKeyPress += CtrlCHandler!;
		if (noneSuccessful || (filesOnServer.IsEmpty && !directoriesToScan.IsEmpty))
		{
			LogWithMarkup(Logger, LogLevel.Error, "Scanning could not complete due to network or other errors.");
			if (UserConfig.PromptToContinue)
				PromptBeforeExit();
			return 1;
		}

		opTimer.Stop();
		bool fileCountIsLow = false;
		if (filesOnServer.Count < 9001)
			fileCountIsLow = true;
		LogMarkupText(Logger, LogLevel.Info,
			$"Scanned {filesOnServer.Count.Pluralize("file").Colorize(HighlightColor)} in {(opTimer.ElapsedMilliseconds + "ms").Colorize(HighlightColor)}.");

		const string comparingFilesMessage = "Comparing files.";
		LogWithMarkup(Logger, LogLevel.Info, comparingFilesMessage, newline: false);
		opTimer.Restart();
		bool                       localAccessSuccessful = true;
		ConcurrentQueue<Exception> localAccessExceptions = new();
		foreach (PathMapping pm in baseDirectoriesToScan)
		{
			DirectoryInfo baseDirDi = new(pm.LocalFullPath);
			Directory.CreateDirectory(pm.LocalFullPath);
			IEnumerable<FileInfo> fileEnumeration = baseDirDi.EnumerateFiles("*",
				new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
			try
			{
				string filepathIdx      = "";
				bool   isDisabledZipmod = false;
				foreach (FileInfo fileondiskinfo in fileEnumeration)
				{
					isDisabledZipmod = fileondiskinfo.Extension == ".zi_mod";
					filepathIdx = isDisabledZipmod
						? Path.ChangeExtension(fileondiskinfo.FullName, ".zipmod").ToLower()
						: filepathIdx = fileondiskinfo.FullName.ToLower();
					if (filesOnServer.TryGetValue(filepathIdx, out PathMapping matchPm) &&
					    (isDisabledZipmod || matchPm.FileSize == fileondiskinfo.Length))
						_ = filesOnServer.TryRemove(matchPm.LocalFullPathLower, out _);
					else if (pm.DeleteClientFiles)
						filesToDelete.Add(fileondiskinfo);
				}
			}
			catch (Exception ex)
			{
				localAccessSuccessful = false;
				LogMarkupText(Logger, LogLevel.Error,
					$"Could not access {pm.LocalFullPath.EscapeMarkup().Colorize(ErrorColorAlt)}. " +
					"Ensure that you have the correct path specified in your configuration and that you have permission to access it."
						.Colorize(ErrorColor));
				localAccessExceptions.Enqueue(ex);
				continue;
			}

			if (!localAccessSuccessful)
				throw new AggregateException(localAccessExceptions);
		}

		foreach (KeyValuePair<string, PathMapping> pmKvp in filesOnServer)
			filesToDownload.Add(PathMappingToFileDownload(pmKvp.Value));
		opTimer.Stop();
		Console.Write(new string(' ', comparingFilesMessage.Length) + '\r');
		LogMarkupText(Logger, LogLevel.Info,
			$"Comparison took {(opTimer.ElapsedMilliseconds + "ms").Colorize(HighlightColor)}.");

		DLStatus.TotalNumberOfFilesToDownload = filesToDownload.Count;
		DLStatus.NumberOfFilesToDownload      = filesToDownload.Count;
		DLStatus.TotalBytesToDownload         = filesToDownload.Select(fileDl => fileDl.TotalFileSize).Sum();
		DLStatus.TotalNumberOfFilesToDownload = filesToDownload.Count;
		DLStatus.NumberOfFilesToDownload      = filesToDownload.Count;
		if (!filesToDownload.IsEmpty || !filesToDelete.IsEmpty)
		{
			string downloadCountSummary = filesToDownload.IsEmpty
				? string.Empty
				: $"{filesToDownload.Count.Pluralize("file").Colorize(HighlightColor)} to download ({DLStatus.TotalBytesToDownloadString.Colorize(HighlightColor)})";
			string deletionCountSummary = filesToDelete.IsEmpty
				? string.Empty
				: $"{filesToDelete.Count.Pluralize("file").Colorize(HighlightColor)} to delete";
			string connector = filesToDownload.IsEmpty || filesToDelete.IsEmpty ? string.Empty : " and ";
			LogMarkupText(Logger, LogLevel.Info, downloadCountSummary + connector + deletionCountSummary + ".");
		}
		else
			LogWithMarkup(Logger, LogLevel.Info, "No files to download or delete.");

		if (!filesToDelete.IsEmpty)
		{
			ConcurrentBag<FileInfo> failedToDelete  = new();
			List<Exception>         failedDeletions = new();
			if (filesToDelete.Count > 100)
			{
				Console.CancelKeyPress -= CtrlCHandler!;
				Console.CancelKeyPress += DeletionCtrlCHandler!;
				LogWithMarkup(Logger, LogLevel.Warn, "There are more than 100 files to delete.");
				if (fileCountIsLow)
					AnsiConsole.WriteLine(
						"There are many files to delete and few found on the server. This is a sign of a serious error and you should press Ctrl-C now.");
				else
					AnsiConsole.WriteLine(
						"This could be due to a large deletion in the bleeding edge pack, but could also be due to an internal error.\n" +
						"Please check the log now and confirm that this seems to be the case. If not, press Ctrl-C now to exit.");
				AnsiConsole.WriteLine("Type 'continue anyway' to continue or press Ctrl-C to abort.");
				while (true)
				{
					string? continueAnyway = Console.ReadLine();
					if (continueAnyway?.Replace("'", null) is "continue anyway")
					{
						LogMarkupText(Logger, LogLevel.Warn,
							$"Proceeding with deletion of {filesToDelete.Count.Colorize(HighlightColor)} files."
								.Colorize(WarningColor));
						break;
					}

					AnsiConsole.WriteLine("Fully type 'continue anyway' to continue or press Ctrl-C to abort.");
				}

				Console.CancelKeyPress -= DeletionCtrlCHandler!;
				Console.CancelKeyPress += CtrlCHandler!;
			}

			if (UserConfig.PromptToContinue && AnsiConsole.Confirm("Show full list of files to delete?"))
			{
				AnsiConsole.MarkupLine("Will delete files:");
				foreach (FileInfo pm in filesToDelete)
					AnsiConsole.MarkupLine(Path.GetRelativePath(UserConfig.GamePath, pm.FullName).EscapeMarkup()
						.Colorize(DeleteColor));
			}

			Logger.Info($"{filesToDelete.Count.Pluralize("file")} marked for deletion.");
			PromptUserToContinue();
			foreach (FileInfo pm in filesToDelete)
				try
				{
					File.Delete(pm.FullName);
					Logger.Info($"Deleted {pm.FullName}.");
				}
				catch (Exception ex)
				{
					failedToDelete.Add(pm);
					failedDeletions.Add(ex);
					AnsiConsole.WriteLine(ex.Message.EscapeMarkup().Colorize(WarningColor));
				}

			LogMarkupText(Logger, LogLevel.Info,
				$"{(filesToDelete.Count - failedToDelete.Count).Pluralize("file").Colorize(HighlightColor)} deleted.");
			Debug.Assert(failedDeletions.Count == failedToDelete.Count);
			if (failedDeletions.Count > 0)
			{
				LogMarkupText(Logger, LogLevel.Error,
					$"{failedToDelete.Count.Pluralize("file").Colorize(ErrorColorAlt)} could not be deleted."
						.Colorize(ErrorColor));
				foreach (Exception ex in failedDeletions)
					Logger.Warn(ex);
				throw new AggregateException(failedDeletions);
			}
		}

		if (!filesToDownload.IsEmpty)
		{
			Dictionary<string, (int TotalFiles, long TotalBytes)> packTotals = new();
			foreach (PathMapping packDir in baseDirectoriesToScan)
				packTotals[packDir.FileName] = (0, 0L);
			if (skipScan) packTotals["Sideloader Modpack"] = (0, 0L);

			foreach (FileDownload fileToDl in filesToDownload)
			{
				string baseName = Path.GetRelativePath(UserConfig.GamePath, fileToDl.LocalPath)
					.RelativeModPathToPackName();
				int  newFileCount = packTotals[baseName].TotalFiles + 1;
				long newByteCount = packTotals[baseName].TotalBytes + fileToDl.TotalFileSize;
				packTotals[baseName] = (newFileCount, newByteCount);
			}

			foreach (string packName in packTotals.Keys.OrderBy(name => name))
			{
				int filecount = packTotals[packName].TotalFiles;
				if (filecount == 0)
					continue;
				long bytecount = packTotals[packName].TotalBytes;
				AnsiConsole.MarkupLine($"- {packName.Colorize(ModpackNameColor)}: "               +
				                       $"{filecount.Pluralize("file").Colorize(HighlightColor)} " +
				                       $"({bytecount.FormatBytes().Colorize(HighlightColor)})");
			}

			if (UserConfig.PromptToContinue && AnsiConsole.Confirm("Show full list of files to download?"))
			{
				foreach (FileDownload fileDl in filesToDownload.OrderBy(fd => fd.LocalPath))
				{
					AnsiConsole.MarkupLine(
						$"{Path.GetRelativePath(UserConfig.GamePath, fileDl.LocalPath).EscapeMarkup().Colorize(FileListingColor)} ({fileDl.TotalFileSize.FormatBytes().Colorize(HighlightColor)})");
					Logger.Debug($"{fileDl.LocalPath}");
				}

				PromptUserToContinue();
			}

			opTimer.Restart();
			DLStatus.TrackTotalCurrentSpeed();
			TotalProgressBar = new ProgressBar((int)(DLStatus.TotalBytesToDownload / 1024), "Downloading files:",
				DefaultTotalProgressBarOptions);
			ConcurrentQueue<DownloadChunk> chunks = new();
			while (filesToDownload.TryTake(out FileDownload currentFileDownload))
			{
				DLStatus.FileDownloadsInformation[currentFileDownload.LocalPath] = new FileDownloadProgressInformation
				{
					FilePath = currentFileDownload.LocalPath, TotalFileSize = currentFileDownload.TotalFileSize
				};
				foreach (DownloadChunk chunk in currentFileDownload.DownloadChunks)
					chunks.Enqueue(chunk);
			}

			int downloadTaskCount = UserConfig.ConnectionInfo.MaxConnections < chunks.Count
				? UserConfig.ConnectionInfo.MaxConnections
				: chunks.Count;
			Logger.Trace("Chunks to download:");
			foreach (DownloadChunk chunk in chunks)
				Logger.Trace($"{chunk.FileName} at offset {chunk.Offset}");
			List<Task>                    downloadTasks         = new();
			List<Task>                    finishedDownloadTasks = new();
			using CancellationTokenSource downloadCts           = new();
			CancellationToken             downloadCt            = downloadCts.Token;
			bool                          downloadCanceled      = false;
			AggregateException?           downloadFailures      = null;
			Console.CancelKeyPress -= CtrlCHandler!;

			DLStatus.DownloadSpeedStopwatch.Start();
			for (int i = 0; i < UserConfig.ConnectionInfo.MaxConnections; i++)
				downloadTasks.Add(Task.Run(
					() => DownloadFileChunks(UserConfig.ConnectionInfo, in chunks, DLStatus.ReportProgress,
						downloadCt), downloadCt));
			try { finishedDownloadTasks = ProcessTasks(downloadTasks, downloadCts); }
			catch (OperationCanceledException) { downloadCanceled = true; }
			catch (AggregateException ex) { downloadFailures      = ex; }

			DLStatus.DownloadSpeedStopwatch.Stop();
			Logger.Debug($"Chunks left after processing: {chunks.Count}");
			Console.CancelKeyPress += CtrlCHandler!;

			foreach (KeyValuePair<string, FileDownloadProgressInformation> progressInfoKvp in DLStatus
				         .FileDownloadsInformation)
			{
				FileDownloadProgressInformation progressInfo = progressInfoKvp.Value;
				if (progressInfo.IsInitialized && !progressInfo.IsComplete)
				{
					progressInfo.Complete(false);
					try { File.Delete(progressInfo.FilePath); }
					catch (Exception ex)
					{
						LogMarkupText(Logger, LogLevel.Error,
							$"Tried to delete incomplete file {progressInfo.FilePath.EscapeMarkup().Colorize(ErrorColor)} during cleanup but encountered an error:"
								.Colorize(ErrorColorAlt));
						LogExceptionAndDisplay(Logger, ex);
					}
				}
			}

			TotalProgressBar.Message = "";
			TotalProgressBar.Dispose();
			opTimer.Stop();
			if (downloadFailures is not null)
			{
				LogWithMarkup(Logger, LogLevel.Error, "Could not download some files. Check the log for error details.",
					ErrorColorAlt);
				foreach (Exception innerEx in downloadFailures.Flatten().InnerExceptions)
					Logger.Warn(innerEx);
			}

			int numberOfDownloadsFinished = DLStatus.TotalNumberOfFilesToDownload - DLStatus.NumberOfFilesToDownload;
			IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> queuedDownloads =
				DLStatus.FileDownloadsInformation.Where(info => !info.Value.IsInitialized);
			IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> canceledDownloads =
				DLStatus.FileDownloadsInformation.Where(info =>
					info.Value.IsInitialized && info.Value.TotalBytesDownloaded < info.Value.TotalFileSize);
			IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> completedDownloads =
				DLStatus.FileDownloadsInformation.Where(info =>
					info.Value.IsInitialized && info.Value.TotalBytesDownloaded == info.Value.TotalFileSize);
			IEnumerable<KeyValuePair<string, FileDownloadProgressInformation>> unfinishedDownloads =
				queuedDownloads.Concat(canceledDownloads);
			long bytesOfCompletedFiles = DLStatus.FileDownloadsInformation
				.Where(info => info.Value.IsInitialized && info.Value.TotalBytesDownloaded == info.Value.TotalFileSize)
				.Sum(info => info.Value.TotalBytesDownloaded);
			if (downloadCanceled)
				LogMarkupText(Logger, LogLevel.Warn,
					$"Canceled download of {unfinishedDownloads.Count().Pluralize("file").Colorize(CancelColor)}" +
					$" ({(DLStatus.TotalBytesDownloaded - bytesOfCompletedFiles).FormatBytes().Colorize(HighlightColor)} wasted).");
			int totalMinutes = (int)Math.Floor(opTimer.Elapsed.TotalMinutes);
			string minutesSummary = totalMinutes switch
			{
				1   => $"{(totalMinutes + " minute").Colorize(HighlightColor)} and ",
				> 2 => $"{(totalMinutes + " minutes").Colorize(HighlightColor)} and ",
				_   => string.Empty
			};
			string secondsSummary = opTimer.Elapsed.Seconds switch
			{
				1 => $"{(opTimer.Elapsed.Seconds + " second").Colorize(HighlightColor)}",
				_ => $"{(opTimer.Elapsed.Seconds + " seconds").Colorize(HighlightColor)}"
			};
			LogMarkupText(Logger, LogLevel.Info,
				$"Completed download of {numberOfDownloadsFinished.Pluralize("file").Colorize(HighlightColor)} ({bytesOfCompletedFiles.FormatBytes().Colorize(HighlightColor)})" +
				$" in {minutesSummary}{secondsSummary}.");
			LogMarkupText(Logger, LogLevel.Info,
				$"Average speed: {((DLStatus.TotalBytesDownloaded / opTimer.Elapsed.TotalSeconds).FormatBytes() + "/s").Colorize(HighlightColor)}");

			bool displaySummary = UserConfig.PromptToContinue && AnsiConsole.Confirm("Display file download summary?");

			void LogSummary(string message)
			{
				if (displaySummary) LogMarkupText(Logger, LogLevel.Info, message);
				else Logger.Info(message);
			}

			if (unfinishedDownloads.Any())
				LogSummary("Canceled downloads:");
			foreach (KeyValuePair<string, FileDownloadProgressInformation> canceledPath in
			         unfinishedDownloads.OrderBy(pm => pm.Key))
				LogSummary(Path.GetRelativePath(UserConfig.GamePath, canceledPath.Key).EscapeMarkup()
					.Colorize(CancelColor));
			if (completedDownloads.Any())
				LogSummary("Completed downloads:");
			foreach (KeyValuePair<string, FileDownloadProgressInformation> completedPath in
			         completedDownloads.OrderBy(pm => pm.Key))
				LogSummary(Path.GetRelativePath(UserConfig.GamePath, completedPath.Key).EscapeMarkup()
					.Colorize(SuccessColor));
		}

		if (!filesToDownload.IsEmpty)
			RaiseInternalFault(Logger, $"There are still {filesToDownload.Count.Pluralize("file")} after processing.");
		LogWithMarkup(Logger, LogLevel.Info, "Finished updating.");
		if (UserConfig.PromptToContinue)
			PromptBeforeExit();
		return 0;
	}
}