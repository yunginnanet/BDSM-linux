using BDSM.Lib;

using NLog;
using NLog.Config;
using NLog.Targets;

using Spectre.Console;

using Layout = NLog.Layouts.Layout;

namespace BDSM;

internal static class LoggingConfiguration
{
	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

	private static readonly FileTarget DefaultLogfileConfig = new("logfile")
	{
		Layout =
			Layout.FromString(
				"[${longdate}]${when:when=exception != null: [${callsite-filename}${literal:text=\\:} ${callsite-linenumber}]} ${level}: ${message}${exception:format=@}"),
		FileName                         = "BDSM.log",
		Footer                           = Layout.FromString("[${longdate}] ${level}: == End BDSM log =="),
		ArchiveOldFileOnStartupAboveSize = 1024 * 1024
	};

	private static readonly FileTarget DebugOrTraceLogfileConfig = new("logfile")
	{
		Layout =
			Layout.FromString(
				"[${longdate}] [${callsite-filename:includeSourcePath=false}${literal:text=\\:} line ${callsite-linenumber}] ${level}: ${message}${exception:format=@}"),
		FileName                         = "BDSM.debug.log",
		Footer                           = Layout.FromString("[${longdate}] ${level}: == End BDSM debug log =="),
		ArchiveOldFileOnStartupAboveSize = 1024 * 1024
	};

	internal static void InitalizeLibraryLoggers(ILogger logger)
	{
		FtpFunctions.InitializeLogger(logger);
		Configuration.InitializeLogger(logger);
		DownloadProgress.InitializeLogger(logger);
		FileDownloadProgressInformation.InitializeLogger(logger);
	}

	internal static NLog.Config.LoggingConfiguration LoadCustomConfiguration(out bool isCustom,
		string loglevelName = "Info") => LoadCustomConfiguration(out isCustom, LogLevel.FromString(loglevelName));

	internal static NLog.Config.LoggingConfiguration LoadCustomConfiguration(out bool isCustom, LogLevel loglevel)
	{
		Logger.Debug("Loading BDSM log configuration.");
		NLog.Config.LoggingConfiguration config;
		isCustom = File.Exists("nlog.config");
		if (isCustom)
			config = new XmlLoggingConfiguration("nlog.config");
		else
		{
			config = new NLog.Config.LoggingConfiguration();
			LogLevel baseLoglevel = loglevel.Ordinal switch
			{
				< 3 => LogLevel.Info,
				_   => loglevel
			};
			config.AddRule(baseLoglevel, LogLevel.Fatal, DefaultLogfileConfig);
			if (loglevel.Ordinal is 0 or 1)
				config.AddRule(LogLevel.Debug, LogLevel.Fatal, DebugOrTraceLogfileConfig);
			if (loglevel.Ordinal is 0)
				config.AddRule(LogLevel.Trace, LogLevel.Fatal, DebugOrTraceLogfileConfig);
		}

		return config;
	}

	internal static void LogWithMarkup(ILogger logger,                 LogLevel logLevel, string message,
		string?                                customColorName = null, bool     newline = true)
	{
		string markupText = message;
		markupText = customColorName is not null
			? markupText.Colorize(customColorName)
			: logLevel.Ordinal switch
			{
				3      => message.Colorize(BDSM.WarningColor),
				4 or 5 => message.Colorize(BDSM.ErrorColor),
				_      => message
			};
		if (newline) AnsiConsole.MarkupLine(markupText);
		else AnsiConsole.Markup(markupText);
		logger.Log(logLevel, message);
	}

	internal static void LogMarkupText(ILogger logger, LogLevel logLevel, string markupText)
	{
		AnsiConsole.MarkupLine(markupText);
		logger.Log(logLevel, markupText.RemoveMarkup());
	}

	internal static void LogExceptionAndDisplay(ILogger logger, Exception ex) =>
		LogExceptionAndDisplay(logger, LogLevel.Error, ex);

	internal static void LogExceptionAndDisplay(ILogger logger, LogLevel logLevel, Exception ex)
	{
		AnsiConsole.WriteException(ex);
		logger.Log(logLevel, ex);
	}
}