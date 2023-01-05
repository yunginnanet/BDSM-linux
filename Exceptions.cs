namespace BDSM;
public record Exceptions
{
	public class FTPOperationException : Exception
	{
		public FTPOperationException() { }
		public FTPOperationException(string? message) : base(message) { }
		public FTPOperationException(string? message, Exception? innerException) : base(message, innerException) { }
	}
	public class FTPConnectionException : FTPOperationException
	{
		public FTPConnectionException() { }

		public FTPConnectionException(string? message) : base(message) { }

		public FTPConnectionException(string? message, Exception? innerException) : base(message, innerException) { }
	}
	public class FTPTaskAbortedException : FTPOperationException
	{
		public FTPTaskAbortedException() : base() { }

		public FTPTaskAbortedException(string? message) : base(message) { }

		public FTPTaskAbortedException(string? message, Exception? innerException) : base(message, innerException) { }
		public override Dictionary<string, string> Data { get; } = new() { { "File", "" } };
	}
}
