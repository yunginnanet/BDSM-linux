using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;

using FluentFTP;
using FluentFTP.Exceptions;

using NLog;

using static BDSM.Lib.Exceptions;

namespace BDSM.Lib;

public static class FtpFunctions
{
	private static readonly FtpFunctionOptions                Options             = new();
	private static readonly ConcurrentDictionary<int, bool?>  ScanQueueWaitStatus = new();
	private static readonly ConcurrentDictionary<string, int> EmptyDirs           = new();

	private static ILogger _logger = LogManager.GetCurrentClassLogger();

	[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members",
		Justification = "For in-depth debugging of FTP operations only. Not usually needed.")]
	private static void LogFtpMessage(FtpTraceLevel trace, string message) => _logger.Log(
		LogLevel.FromOrdinal((int)trace + 1),
		$"[Managed thread {Environment.CurrentManagedThreadId}] " + "[FluentFTP] " + message);

	public static void InitializeLogger(ILogger parent) => _logger = parent;

	public static FtpClient SetupFtpClient(Configuration.RepoConnectionInfo repoinfo) =>
		new(repoinfo.Address, repoinfo.Username, repoinfo.Password, repoinfo.Port)
		{
			Config = BetterRepackRepositoryDefinitions.DefaultRepoConnectionConfig, Encoding = Encoding.UTF8
			//LegacyLogger = LogFTPMessage
		};
//	new(repoinfo.Address, repoinfo.Username, repoinfo.EffectivePassword, repoinfo.Port) { Config = BetterRepackRepositoryDefinitions.DefaultRepoConnectionConfig, Encoding = Encoding.UTF8 };

	public static FtpClient DefaultSideloaderClient() =>
		SetupFtpClient(BetterRepackRepositoryDefinitions.DefaultConnectionInfo);

	private static bool TryConnect(FtpClient client, int maxRetries = 3)
	{
		int  tid     = Environment.CurrentManagedThreadId;
		int  retries = 0;
		bool success;
		while (true)
		{
			_logger.Debug($"[Managed thread {tid}] TryConnect hit with {retries} retries.");
			try
			{
				client.Connect();
				success = true;
				_logger.Debug($"[Managed thread {tid}] An FTP connection was successful");
				break;
			}
			catch (FtpCommandException fcex) when (fcex.CompletionCode is not "421")
			{
				switch (fcex.ResponseType)
				{
					case FtpResponseType.TransientNegativeCompletion:
						retries++;
						_logger.Debug(
							$"[Managed thread {tid}] Failed to establish an FTP connection with a transient error at attempt {retries}.");
						Thread.Sleep(1000);
						break;
					case FtpResponseType.PermanentNegativeCompletion:
						_logger.Warn(
							$"[Managed thread {tid}] Failed to establish an FTP connection with a permanent error: {fcex.Message}");
						throw;
					case FtpResponseType.PositivePreliminary or FtpResponseType.PositiveCompletion
						or FtpResponseType.PositiveIntermediate:
						_logger.Error(fcex,
							$"[Managed thread {tid}] FtpCommandException was thrown with a positive response.");
						throw new BDSMInternalFaultException(
							"Don't know how to handle FtpCommandException with a positive response.", fcex);
				}

				if (retries > maxRetries)
				{
					client.Dispose();
					throw;
				}
			}
			catch (FtpCommandException fcex)
			{
				_logger.Warn($"{fcex.Message}");
				client.Dispose();
				throw;
			}
			catch (FtpException fex)
			{
				_logger.Warn(
					$"[Managed thread {tid}] Failed to establish an FTP connection with an unknown FTP error: {fex.Message}");
				client.Dispose();
				throw;
			}
			catch (Exception tex) when (tex is TimeoutException or IOException)
			{
				retries++;
				if (retries > maxRetries)
				{
					string errorMessage = $"[Managed thread {tid}] " + (tex is TimeoutException
						? "FTP connection attempt timed out."
						: "FTP connection had an I/O error.");
					_logger.Error(tex, errorMessage);
					throw new FtpOperationException(errorMessage, tex);
				}

				Thread.Sleep(2000);
			}
			catch (Exception ex)
			{
				_logger.Debug(
					$"[Managed thread {tid}] Failed to establish an FTP connection with an unknown error: {ex.Message}");
				client.Dispose();
				throw;
			}
		}

		return success;
	}

	public static void GetFilesOnServer(ref ConcurrentBag<PathMapping> pathsToScan,
		ref ConcurrentDictionary<string, PathMapping> filesFound, Configuration.RepoConnectionInfo repoinfo,
		CancellationToken ct)
	{
		int tid = Environment.CurrentManagedThreadId;
		ScanQueueWaitStatus[tid] = false;
		bool shouldWaitForQueue;
		try { ct.ThrowIfCancellationRequested(); }
		catch (Exception)
		{
			ScanQueueWaitStatus[tid] = null;
			throw;
		}

		ConcurrentBag<PathMapping> files = new();
		PathMapping                pathmap;
		FtpException?              lastFtpException      = null;
		List<Exception>            accumulatedExceptions = new();
		using FtpClient            downloadClient        = SetupFtpClient(repoinfo);
		ScanQueueWaitStatus[tid] = null;
		if (!TryConnect(downloadClient))
			throw new FtpConnectionException();

		while (!ct.IsCancellationRequested)
		{
			ScanQueueWaitStatus[tid] = false;
			if (accumulatedExceptions.Count > 2)
			{
				ScanQueueWaitStatus[tid] = null;
				downloadClient.Dispose();
				throw new AggregateException(accumulatedExceptions);
			}

			try { ct.ThrowIfCancellationRequested(); }
			catch (Exception)
			{
				ScanQueueWaitStatus[tid] = null;
				throw;
			}

			if (!pathsToScan.TryTake(out pathmap))
			{
				ScanQueueWaitStatus[tid] = true;
				Thread.Sleep(500);
				shouldWaitForQueue = ScanQueueWaitStatus.Values.Count(waiting => waiting ?? true) <
				                     ScanQueueWaitStatus.Count;
				if (shouldWaitForQueue)
					continue;
				break;
			}

			try { ct.ThrowIfCancellationRequested(); }
			catch (Exception)
			{
				ScanQueueWaitStatus[tid] = null;
				throw;
			}

			string        remotepath = pathmap.RemoteFullPath;
			string        localpath  = pathmap.LocalFullPath;
			FtpListItem[] scannedFiles;
			int           scanAttempts = 0;
			int           timeouts     = 0;

			try { ct.ThrowIfCancellationRequested(); }
			catch (Exception)
			{
				ScanQueueWaitStatus[tid] = null;
				throw;
			}

			try { scannedFiles = downloadClient.GetListing(remotepath); }
			catch (Exception ex) when (ex is FtpCommandException or IOException
				                           or SocketException)
			{
				pathsToScan.Add(pathmap);
				scanAttempts++;
				Thread.Sleep(100);
				if (scanAttempts == 3)
				{
					downloadClient.Dispose();
					ScanQueueWaitStatus[tid] = null;
					throw;
				}

				continue;
			}
			catch (FtpException fex) when (fex.Message != lastFtpException?.Message)
			{
				accumulatedExceptions.Add(fex);
				pathsToScan.Add(pathmap);
				continue;
			}
			catch (TimeoutException)
			{
				pathsToScan.Add(pathmap);
				timeouts++;
				Thread.Sleep(100);
				if (timeouts == 5)
				{
					downloadClient.Dispose();
					ScanQueueWaitStatus[tid] = null;
					throw;
				}

				continue;
			}

			if (scannedFiles.Length == 0)
			{
				string emptyDir = pathmap.RemoteFullPath;
				EmptyDirs[emptyDir] = EmptyDirs.TryGetValue(emptyDir, out int retries) ? retries + 1 : 1;
				if (EmptyDirs[emptyDir] > 2)
					_ = EmptyDirs.TryRemove(emptyDir, out _);
				else
					pathsToScan.Add(pathmap);
				continue;
			}

			if (EmptyDirs.TryGetValue(pathmap.RemoteFullPath, out int _))
				_logger.Warn($"[Managed thread {tid}] Recovered from a faulty directory listing.");
			accumulatedExceptions.Clear();
			scanAttempts = 0;
			if (scannedFiles is null)
				throw new FtpOperationException(
					$"Tried to get a listing for {pathmap.RemoteFullPath} but apparently it doesn't exist.");
			foreach (FtpListItem item in scannedFiles)
				switch (item.Type)
				{
					case FtpObjectType.File:
						pathmap = pathmap with
						{
							LocalRelativePath = string.Join('\\', pathmap.LocalRelativePath,  item.Name),
							RemoteRelativePath = string.Join('/', pathmap.RemoteRelativePath, item.Name),
							FileSize = item.Size
						};
						_ = filesFound.TryAdd(pathmap.LocalFullPathLower, pathmap);
						break;
					case FtpObjectType.Directory:
						pathmap = pathmap with
						{
							LocalRelativePath = string.Join('\\', pathmap.LocalRelativePath,  item.Name),
							RemoteRelativePath = string.Join('/', pathmap.RemoteRelativePath, item.Name)
						};
						pathsToScan.Add(pathmap);
						break;
					case FtpObjectType.Link:
						break;
				}
		}

		ScanQueueWaitStatus[tid] = null;
		downloadClient.Dispose();
		ct.ThrowIfCancellationRequested();
	}

	[Obsolete(
		"This should no longer be needed with the new simplified configuration and is likely to be removed in a future release.")]
	public static List<string> SanityCheckBaseDirectories(IEnumerable<PathMapping> entriesToCheck,
		Configuration.RepoConnectionInfo                                           repoinfo)
	{
		List<string>    badEntries   = new();
		using FtpClient sanityClient = SetupFtpClient(repoinfo);
		sanityClient.Connect();
		foreach (PathMapping entry in entriesToCheck)
			if (!sanityClient.FileExists(entry.RemoteFullPath))
				badEntries.Add(entry.RemoteFullPath);
		sanityClient.Disconnect();
		sanityClient.Dispose();
		return badEntries;
	}

	public static void DownloadFileChunks(Configuration.RepoConnectionInfo repoinfo,
		in ConcurrentQueue<DownloadChunk> chunks, in Action<ChunkDownloadProgressInformation, string> reportprogress,
		CancellationToken ct)
	{
		int             tid             = Environment.CurrentManagedThreadId;
		byte[]          buffer          = new byte[Options.BufferSize];
		FileStream      localFilestream = null!;
		using FtpClient client          = SetupFtpClient(repoinfo);

		void Cleanup()
		{
			client.Dispose();
			localFilestream.Dispose();
		}

		ChunkDownloadProgressInformation? progressinfo             = null;
		DownloadChunk                     chunk                    = default;
		Stopwatch                         currentStopwatch         = new();
		bool                              canceled                 = ct.IsCancellationRequested;
		bool                              isReportedOrJustStarting = true;
		while (!canceled)
		{
			if (!isReportedOrJustStarting)
				Debugger.Break();
			isReportedOrJustStarting = false;
			if (!TryConnect(client))
			{
				_logger.Warn($"[Managed thread {tid}] An FTP connection failed to be established.");
				throw new FtpConnectionException();
			}

			if (!chunks.TryDequeue(out chunk))
			{
				_logger.Debug($"[Managed thread {tid}] No more chunks left.");
				break;
			}

			_logger.Debug($@"A chunk was taken: {chunk.FileName} at {chunk.Offset}");
			if (chunk.LocalPath != localFilestream.Name)
			{
				localFilestream.Dispose();
				_ = Directory.CreateDirectory(Path.GetDirectoryName(chunk.LocalPath)!);
				localFilestream = new FileStream(chunk.LocalPath, FileMode.OpenOrCreate, FileAccess.Write,
					FileShare.ReadWrite);
			}

			Stream ftpFilestream;
			int    connectionRetries = 0;
			while (true)
				try
				{
					ftpFilestream = client.OpenRead(chunk.RemotePath, FtpDataType.Binary, chunk.Offset,
						chunk.Offset + chunk.Length);
					break;
				}
				catch (Exception) when (connectionRetries <= 2) { connectionRetries++; }
				catch (Exception ex)
				{
					Cleanup();
					_logger.Debug(ex,
						$"[Managed thread {tid}] A chunk was requeued because of a failed FTP download connection: {chunk.FileName} at {chunk.Offset}");
					chunks.Enqueue(chunk);
					throw new FtpOperationException(
						$"The FTP data stream could not be read for {chunk.FileName} at {chunk.Offset}", ex);
				}

			localFilestream.Lock(chunk.Offset, chunk.Length);
			localFilestream.Position = chunk.Offset;
			int       totalChunkBytes = 0;
			int       remainingBytes  = chunk.Length;
			int       bytesToProcess;
			Stopwatch writeTime = new();
			isReportedOrJustStarting = true;
			while (true)
			{
				if (!isReportedOrJustStarting)
					Debugger.Break();
				isReportedOrJustStarting = false;
				if (ct.IsCancellationRequested)
				{
					_logger.Debug(
						$"[Managed thread {tid}] Cancellation was requested after an FTP download connection was established.");
					canceled = true;
					break;
				}

				bytesToProcess = buffer.Length < remainingBytes ? buffer.Length : remainingBytes;
				try
				{
					ftpFilestream.ReadExactly(buffer, 0, bytesToProcess);
					remainingBytes  -= bytesToProcess;
					totalChunkBytes += bytesToProcess;
					localFilestream.Write(buffer, 0, bytesToProcess);
				}
				catch (Exception ex)
				{
					ftpFilestream.Dispose();
					localFilestream.Unlock(chunk.Offset, chunk.Length);
					localFilestream.Dispose();
					client.Disconnect();
					client.Dispose();
					string message = ex switch
					{
						OperationCanceledException => "The download operation was canceled.",
						FtpConnectionException     => "Could not connect to the server.",
						FtpTaskAbortedException    => "write timeout",
						_                          => $"Unexpected error: {ex.Message}"
					};
					_logger.Debug($"[Managed thread {tid}] A write operation failed. ({message})");
					throw;
				}

				progressinfo = new ChunkDownloadProgressInformation
				{
					BytesDownloaded = bytesToProcess,
					TimeElapsed     = currentStopwatch.Elapsed,
					TotalChunkSize  = chunk.Length
				};
				localFilestream.Flush();
				isReportedOrJustStarting = true;
				if (totalChunkBytes > chunk.Length)
					throw new BDSMInternalFaultException("Total chunk bytes is greater than the chunk length.");

				if (totalChunkBytes == chunk.Length) break;
				reportprogress((ChunkDownloadProgressInformation)progressinfo, chunk.LocalPath);
				if (ct.IsCancellationRequested)
				{
					_logger.Debug(
						$"[Managed thread {tid}] Cancellation was requested after a chunk download was complete.");
					canceled = true;
					break;
				}
			}

			try { localFilestream.Unlock(chunk.Offset, chunk.Length); }
			catch (IOException ex) when (ex.Message.StartsWith("The segment is already unlocked.")) { }

			ftpFilestream.Dispose();
			currentStopwatch.Stop();
			if (!canceled)
			{
				_logger.Debug(
					$"[Managed thread {tid}] Reporting completion of a chunk (line 293): {chunk.FileName} at {chunk.Offset}");
				reportprogress((ChunkDownloadProgressInformation)progressinfo!, chunk.LocalPath);
				isReportedOrJustStarting = true;
			}

			_logger.Debug(
				$"""[Managed thread {tid}] Exiting chunk processing loop{(canceled ? " (canceled)" : "")}: {chunk.FileName} at {chunk.Offset}""");
		}

		try { localFilestream.Unlock(chunk.Offset, chunk.Length); }
		catch (IOException ex) when (ex.Message.StartsWith("The segment is already unlocked.")) { }

		localFilestream.Flush();
		localFilestream.Dispose();
		client.Disconnect();
		client.Dispose();
		if (chunk.FileName is not null)
		{
			_logger.Debug(
				$"[Managed thread {tid}] Reporting completion of a chunk (line 310): {chunk.FileName} at {chunk.Offset}");
			reportprogress((ChunkDownloadProgressInformation)progressinfo!, chunk.LocalPath);
/*
			is_reported_or_just_starting = true;
*/
		}
	}

	public readonly record struct FtpFunctionOptions
	{
		public FtpFunctionOptions() { }
		public int BufferSize { get; init; } = 65536;
	}
}