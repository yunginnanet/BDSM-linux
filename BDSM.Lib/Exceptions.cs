namespace BDSM.Lib;

public abstract record Exceptions
{
	public class BDSMInternalFaultException : Exception
	{
		private const string BugReportSuffix =
			" Please file a bug report and provide this information. https://github.com/yunginnanet/BDSM-linux/issues\r\n";

		internal BDSMInternalFaultException(string totalChunkBytesIsGreaterThanTheChunkLength) { }

		internal BDSMInternalFaultException(string? message, bool includeBugReportLink) : base(message +
			(includeBugReportLink ? "" : BugReportSuffix))
		{
		}

		internal BDSMInternalFaultException(string? message, Exception? innerException,
			bool includeBugReportLink = true) : base(message + (includeBugReportLink ? "" : BugReportSuffix),
			innerException)
		{
		}
	}

	public class FtpOperationException : Exception
	{
		protected FtpOperationException() { }
		public FtpOperationException(string? message) : base(message) { }
		public FtpOperationException(string? message, Exception? innerException) : base(message, innerException) { }
	}

	public class FtpConnectionException : FtpOperationException
	{
		public FtpConnectionException() { }

		public FtpConnectionException(string? message) : base(message) { }

		public FtpConnectionException(string? message, Exception? innerException) : base(message, innerException) { }
	}

	public abstract class FtpTaskAbortedException : FtpOperationException
	{
		protected FtpTaskAbortedException()
		{
		}

		protected FtpTaskAbortedException(string? message) : base(message) { }

		protected FtpTaskAbortedException(string? message, Exception? innerException) : base(message, innerException)
		{
		}

		public override Dictionary<string, string> Data { get; } = new() { { "File", "" } };
	}
}