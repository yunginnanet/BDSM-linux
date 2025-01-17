﻿using System.Collections.Concurrent;
using System.Diagnostics;

using BDSM.Lib;

using NLog;

using ShellProgressBar;

namespace BDSM;

internal record FileDownloadProgressInformation
{
	private static  ILogger          logger = LogManager.CreateNullLogger();
	public required string           FilePath { get; init; }
	public          long             TotalBytesDownloaded { get; set; }
	public          string           TotalBytesDownloadedString => TotalBytesDownloaded.FormatBytes();
	private         Stopwatch        TotalTimeStopwatch { get; } = new();
	public          TimeSpan         TotalTimeElapsed => TotalTimeStopwatch.Elapsed;
	public          long             CurrentBytesDownloaded { get; set; } = 0;
	public          TimeSpan         CurrentTimeElapsed { get; set; } = new();
	public          double           CurrentSpeed { get; private set; }
	public          string           CurrentSpeedString => CurrentSpeed.FormatBytes() + "/s";
	public          Stopwatch        ProgressUpdateStopwatch { get; } = new();
	public          TimeSpan         ETA => new(0, 0, 0, 0, (int)Math.Round(TotalFileSize / AverageSpeed * 1000, 0));
	public          long             PreviousBytesDownloaded { get; private set; }
	public required long             TotalFileSize { get; init; }
	public          string           TotalFileSizeString => TotalFileSize.FormatBytes();
	public          ChildProgressBar FileProgressBar { get; private set; } = null!;

	public double AverageSpeed =>
		TotalTimeElapsed.TotalSeconds != 0 ? TotalBytesDownloaded / TotalTimeElapsed.TotalSeconds : 0;

	public        string AverageSpeedString               => Math.Round(AverageSpeed, 2).FormatBytes() + "/s";
	public        bool   IsInitialized                    { get; private set; }
	public        bool   IsComplete                       { get; private set; }
	public        bool   CompletedSuccessfully            { get; private set; }
	public static void   InitializeLogger(ILogger parent) => logger = parent;

	private void TrackCurrentSpeed()
	{
		logger.Debug($"Tracking current speed for {FilePath}.");
		while (TotalBytesDownloaded < TotalFileSize)
		{
			PreviousBytesDownloaded = TotalBytesDownloaded;
			Thread.Sleep(1000);
			CurrentSpeed = Math.Round((double)(TotalBytesDownloaded - PreviousBytesDownloaded), 2);
		}

		CurrentSpeed = 0;
	}

	internal void Initialize()
	{
		if (IsInitialized) return;
		TotalTimeStopwatch.Start();
		FileProgressBar = DownloadProgress.TotalProgressBar.Spawn((int)(TotalFileSize / 1024),
			$"{FilePath} | Awaiting download", DownloadProgress.DefaultChildProgressBarOptions);
		ProgressUpdateStopwatch.Start();
		_             = Task.Run(TrackCurrentSpeed);
		IsInitialized = true;
		logger.Debug($"Download of {FilePath} was initialized.");
	}

	internal void Complete(bool successful)
	{
		CompletedSuccessfully = successful;
		TotalTimeStopwatch.Stop();
		ProgressUpdateStopwatch.Stop();
		FileProgressBar.Dispose();
		IsComplete = true;
		logger.Debug($"""Download of {FilePath} was completed {(successful ? "successfully" : "unsuccessfully")}""");
	}
}

internal static class DownloadProgress
{
	internal const  int         UPDATE_INTERVAL_MILLISECONDS = 100;
	private static  ILogger     logger                       = LogManager.CreateNullLogger();
	internal static ProgressBar TotalProgressBar             = null!;

	internal static readonly ProgressBarOptions DefaultTotalProgressBarOptions = new()
	{
		CollapseWhenFinished  = true,
		ShowEstimatedDuration = true,
		DisplayTimeInRealTime = false,
		EnableTaskBarProgress = false,
		ProgressCharacter     = ' '
	};

	internal static readonly ProgressBarOptions DefaultChildProgressBarOptions = new()
	{
		CollapseWhenFinished  = true,
		ShowEstimatedDuration = true,
		DisplayTimeInRealTime = false,
		ProgressBarOnBottom   = true,
		ProgressCharacter     = '─'
	};

	private static           bool                TrackingTotalCurrentSpeed;
	internal static readonly TotalDownloadStatus DLStatus = new();
	public static            void                InitializeLogger(ILogger parent) => logger = parent;

	internal class TotalDownloadStatus
	{
		internal readonly Stopwatch DownloadSpeedStopwatch = new();

		internal readonly ConcurrentDictionary<string, FileDownloadProgressInformation>
			FileDownloadsInformation = new();

		internal readonly Stopwatch ProgressUpdateStopwatch = Stopwatch.StartNew();
		internal          int       NumberOfFilesToDownload;
		internal          long      TotalBytesDownloaded;
		internal          long      TotalBytesToDownload = 0;
		internal          double    TotalCurrentSpeed;
		internal          int       TotalNumberOfFilesToDownload = 0;
		internal          string    TotalBytesToDownloadString => TotalBytesToDownload.FormatBytes();
		internal          string    TotalBytesDownloadedString => TotalBytesDownloaded.FormatBytes();
		internal          string    TotalCurrentSpeedString    => TotalCurrentSpeed.FormatBytes() + "/s";

		internal double TotalDownloadSpeed => DownloadSpeedStopwatch.Elapsed.TotalSeconds != 0
			? TotalBytesDownloaded / DownloadSpeedStopwatch.Elapsed.TotalSeconds
			: 0;

		internal string   TotalDownloadSpeedString => TotalDownloadSpeed.FormatBytes() + "/s";
		internal TimeSpan ETA => new(0, 0, 0, 0, (int)Math.Round(TotalBytesToDownload / TotalDownloadSpeed * 1000, 0));

		internal void TrackTotalCurrentSpeed()
		{
			if (TrackingTotalCurrentSpeed) return;
			_ = Task.Run(() =>
			{
				while (NumberOfFilesToDownload > 0)
				{
					long previous_bytes_downloaded = TotalBytesDownloaded;
					Thread.Sleep(UPDATE_INTERVAL_MILLISECONDS);
					TotalCurrentSpeed =
						Math.Round(
							(double)((TotalBytesDownloaded - previous_bytes_downloaded) *
							         (1000 / UPDATE_INTERVAL_MILLISECONDS)), 2);
				}

				TotalCurrentSpeed = 0;
			});
			TrackingTotalCurrentSpeed = true;
		}

		internal void ReportProgress(ChunkDownloadProgressInformation progressinfo, string filepath)
		{
			if (filepath is null)
				throw new ArgumentNullException(filepath, "A chunk was null.");
			lock (DLStatus.FileDownloadsInformation)
			{
				FileDownloadProgressInformation file_download_progress = DLStatus.FileDownloadsInformation[filepath];
				file_download_progress.Initialize();
				file_download_progress.TotalBytesDownloaded += progressinfo.BytesDownloaded;

				if (file_download_progress.TotalBytesDownloaded > file_download_progress.TotalFileSize)
					throw new BDSM.BDSMInternalFaultException("Total chunk bytes is greater than the chunk length.");
				if (file_download_progress.TotalBytesDownloaded == file_download_progress.TotalFileSize)
				{
					Debug.Assert(DLStatus.NumberOfFilesToDownload > 0);
					_ = Interlocked.Decrement(ref DLStatus.NumberOfFilesToDownload);
					logger.Debug(
						$"Completed download of a file: {Path.GetRelativePath(BDSM.UserConfig.GamePath, filepath)}");
					logger.Debug($"""{DLStatus.NumberOfFilesToDownload.Pluralize("file")} remaining.""");
					lock (TotalProgressBar)
					{
						file_download_progress.FileProgressBar.Dispose();
						file_download_progress.Complete(true);
					}
				}
				else
				{
					if (file_download_progress.ProgressUpdateStopwatch.ElapsedMilliseconds >
					    UPDATE_INTERVAL_MILLISECONDS)
					{
						string file_progress_message =
							$"{Path.GetFileName(filepath)} | {file_download_progress.TotalBytesDownloadedString} / {file_download_progress.TotalFileSizeString} (Current speed: {file_download_progress.CurrentSpeedString})";
						file_download_progress.ProgressUpdateStopwatch.Restart();
						file_download_progress.FileProgressBar.Tick(
							(int)(file_download_progress.TotalBytesDownloaded / 1024), file_download_progress.ETA,
							file_progress_message);
					}
				}

				FileDownloadsInformation[filepath] = file_download_progress;
			}

			_ = Interlocked.Add(ref DLStatus.TotalBytesDownloaded, progressinfo.BytesDownloaded);
			int downloads_finished = DLStatus.TotalNumberOfFilesToDownload - DLStatus.NumberOfFilesToDownload;
			int downloads_in_progress = DLStatus.FileDownloadsInformation.Count(info =>
				info.Value.IsInitialized && info.Value.TotalBytesDownloaded < info.Value.TotalFileSize);
			int downloads_in_queue = FileDownloadsInformation.Count(info => !info.Value.IsInitialized);

			if (ProgressUpdateStopwatch.ElapsedMilliseconds > UPDATE_INTERVAL_MILLISECONDS)
				lock (TotalProgressBar)
				{
					string total_progress_message =
						$"Downloading files ({downloads_finished} done / {downloads_in_progress} in progress / {downloads_in_queue} remaining): " +
						$"{DLStatus.TotalBytesDownloadedString} / {DLStatus.TotalBytesToDownloadString} " +
						$"(Current speed: {TotalCurrentSpeedString}) " +
						$"(Average speed: {(DLStatus.TotalBytesDownloaded / DLStatus.DownloadSpeedStopwatch.Elapsed.TotalSeconds).FormatBytes()}/s)";
					TotalProgressBar.Tick((int)(DLStatus.TotalBytesDownloaded / 1024), ETA, total_progress_message);
					ProgressUpdateStopwatch.Restart();
				}
		}
	}
}