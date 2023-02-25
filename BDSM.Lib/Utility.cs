using System.Collections.Concurrent;
using System.Collections.Immutable;

using static BDSM.Lib.Configuration;

namespace BDSM.Lib;

public static class Utility
{
	public static ConcurrentBag<PathMapping> GetPathMappingsFromSkipScanConfig(SkipScanConfiguration config,
		FullUserConfiguration                                                                        userconfig)
	{
		ConcurrentBag<PathMapping> mappings = new();
		foreach (string pathmap in config.FileMappings)
		{
			string[] mapSplit = pathmap.Split(" | ");
			PathMapping map = new()
			{
				RootPath           = userconfig.ConnectionInfo.RootPath,
				RemoteRelativePath = mapSplit[0],
				GamePath           = userconfig.GamePath,
				LocalRelativePath  = mapSplit[1],
				FileSize           = null,
				DeleteClientFiles  = false
			};
			mappings.Add(map);
		}

		return mappings;
	}

	public static FileDownload PathMappingToFileDownload(PathMapping pm)
	{
		List<DownloadChunk> chunks    = new();
		long                filesize  = (long)pm.FileSize!;
		const int           chunksize = 1024 * 1024 * 10;
		(long fullChunks, long remainingBytes) = Math.DivRem(filesize, chunksize);
		long numChunks = fullChunks + (remainingBytes > 0 ? 1 : 0);
		for (int i = 0; i < numChunks; i++)
		{
			long offset    = i * (long)chunksize;
			long remaining = filesize - offset;
			int  length    = remaining > chunksize ? chunksize : (int)remaining;
			DownloadChunk chunk = new()
			{
				LocalPath = pm.LocalFullPath, RemotePath = pm.RemoteFullPath, Offset = offset, Length = length
			};
			chunks.Add(chunk);
		}

		return new FileDownload
		{
			LocalPath      = pm.LocalFullPath,
			RemotePath     = pm.RemoteFullPath,
			TotalFileSize  = filesize,
			ChunkSize      = chunksize,
			NumberOfChunks = (int)numChunks,
			DownloadChunks = chunks.ToImmutableArray()
		};
	}

	public static string RelativeModPathToPackName(this string relativeLocalPath)
	{
		string[] pathParts = relativeLocalPath.Split('\\');
		return pathParts[0] == "UserData" ? pathParts[0] : pathParts[1];
	}

	public static string FormatBytes(this int  numberOfBytes) => FormatBytes((double)numberOfBytes);
	public static string FormatBytes(this long numberOfBytes) => FormatBytes((double)numberOfBytes);

	public static string FormatBytes(this double numberOfBytes) =>
		numberOfBytes switch
		{
			< 1100  * 1           => $"{Math.Round(numberOfBytes,                        2):N2} B",
			< 1100  * 1024        => $"{Math.Round(numberOfBytes / 1024,                 2):N2} KiB",
			< 1100  * 1024 * 1024 => $"{Math.Round(numberOfBytes / (1024 * 1024),        2):N2} MiB",
			>= 1100 * 1024 * 1024 => $"{Math.Round(numberOfBytes / (1024 * 1024 * 1024), 2):N2} GiB",
			double.NaN            => "unknown"
		};

	public static string Pluralize(this int quantity, string suffix) =>
		quantity == 1 ? quantity + " " + suffix : quantity + " " + suffix + "s";

	public static void PromptUser(string message)
	{
		Console.Write(message);
		_ = Console.ReadKey(true);
		Console.Write('\r' + new string(' ', message.Length) + '\r');
	}

	public static void PromptUserToContinue() => PromptUser("Press any key to continue or Ctrl-C to abort.");
	public static void PromptBeforeExit()     => PromptUser("Press any key to exit.");
}