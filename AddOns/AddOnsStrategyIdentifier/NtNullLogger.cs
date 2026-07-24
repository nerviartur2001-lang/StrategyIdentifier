#region Using declarations
using System;
using NinjaTrader.Cbi;
#endregion

//This namespace holds Add ons in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.NtNullLogger
{
    // ==========================================================
	#region NtNullLogger	
	
	public enum LogLevel { Debug = 0, Info = 1, Error = 2, Off = 3 }

	public interface IStrategyLogger
	{
	    LogLevel MinLevel { get; set; }
	    PrintTo OutputTab { get; set; }
	    bool IsDebugEnabled { get; }
	    void Debug(string message);
	    void Info(string message);
	    void Error(string message);
	}

	public sealed class NtLogger : IStrategyLogger
	{
	    public LogLevel MinLevel { get; set; } = LogLevel.Error;
	
	    public PrintTo OutputTab { get; set; } = PrintTo.OutputTab1;
	
	    public bool IsDebugEnabled => MinLevel <= LogLevel.Debug;
	
	    public void Debug(string message) { if (MinLevel <= LogLevel.Debug) Write("DEBUG", message); }
	
	    public void Info(string message) { if (MinLevel <= LogLevel.Info) Write("INFO", message); }
	
	    public void Error(string message) { if (MinLevel <= LogLevel.Error) Write("ERROR", message); }
	
	    private void Write(string level, string message)
	    {
	        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff", Core.Globals.GeneralOptions.CurrentCulture);
	        int threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
	        NinjaTrader.Code.Output.Process($"[{timestamp}] [T{threadId}] [{level}] {message}", OutputTab);    
	    }
	}

	public sealed class NullLogger : IStrategyLogger
	{
	    public static readonly NullLogger Instance = new NullLogger();
	    private NullLogger() { }
	    public LogLevel MinLevel { get => LogLevel.Off; set { } }
	    public PrintTo OutputTab { get => PrintTo.OutputTab1; set { } }
	    public bool IsDebugEnabled => false;
	    public void Debug(string message) { }
	    public void Info(string message) { }
	    public void Error(string message) { }
	}
	
	#endregion
    // ==========================================================	
}

