using BDSM.Lib;

using NLog;

using Spectre.Console;

namespace BDSM;
internal static class LoggingConfiguration
{
	internal static void InitalizeLibraryLoggers(ILogger logger)
	{
		FTPFunctions.SetLogger(logger);
		//Configuration.SetLogger(logger);
	}
	internal static readonly NLog.Targets.FileTarget default_logfile_config = new("logfile")
	{
		Layout = NLog.Layouts.Layout.FromString("[${longdate}]${when:when=exception != null: [${callsite-filename}${literal:text=\\:} ${callsite-linenumber}]} ${level}: ${message}${exception:format=StackTrace,Data}"),
		FileName = "BDSM.log",
		Footer = NLog.Layouts.Layout.FromString("[${longdate}] ${level}: == End BDSM log =="),
		ArchiveOldFileOnStartupAboveSize = 1024 * 1024
	};
	internal static NLog.Config.LoggingConfiguration LoadCustomConfiguration(out bool is_custom)
	{
		NLog.Config.LoggingConfiguration config;
		try
		{
			config = new NLog.Config.XmlLoggingConfiguration("nlog.config");
			is_custom = true;
			return config;
		}
		catch (Exception ex) when (ex is NLogConfigurationException or FileNotFoundException)
		{
			config = new();
			config.AddRule(LogLevel.Info, LogLevel.Fatal, default_logfile_config);
			is_custom = false;
			return config;
		}
	}
	internal static void LogMarkupText(ILogger logger, LogLevel log_level, string markup_text, bool newline = true)
	{
		if (newline)
			AnsiConsole.MarkupLine(markup_text);
		else
			AnsiConsole.Markup(markup_text);
		logger.Log(log_level, Markup.Remove(markup_text));
	}
	internal static void LogException(ILogger logger, Exception ex) => LogException(logger, LogLevel.Error, ex);
	internal static void LogException(ILogger logger, LogLevel log_level, Exception ex)
	{
		AnsiConsole.WriteException(ex);
		logger.Log(log_level, ex);
	}
}
