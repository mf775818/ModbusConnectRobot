using System;

namespace ModbusConnectRobot.Events
{
    /// <summary>
    /// 連接狀態改變事件參數
    /// </summary>
    public class ConnectionStateChangedEventArgs : EventArgs
    {
        public bool IsConnected { get; }
        public string ErrorMessage { get; }
        public DateTime Timestamp { get; }

        public ConnectionStateChangedEventArgs(bool isConnected, string errorMessage = null)
        {
            IsConnected = isConnected;
            ErrorMessage = errorMessage;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// 斷線事件參數
    /// </summary>
    public class DisconnectedEventArgs : EventArgs
    {
        public string Reason { get; }
        public DateTime Timestamp { get; }
        public bool ShouldAutoReconnect { get; set; }

        public DisconnectedEventArgs(string reason, bool shouldAutoReconnect = true)
        {
            Reason = reason;
            ShouldAutoReconnect = shouldAutoReconnect;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// 重連進度事件參數
    /// </summary>
    public class ReconnectProgressEventArgs : EventArgs
    {
        public int AttemptNumber { get; }
        public int MaxAttempts { get; }
        public TimeSpan NextRetryDelay { get; }
        public string StatusMessage { get; }

        public ReconnectProgressEventArgs(int attemptNumber, int maxAttempts, TimeSpan nextRetryDelay, string statusMessage)
        {
            AttemptNumber = attemptNumber;
            MaxAttempts = maxAttempts;
            NextRetryDelay = nextRetryDelay;
            StatusMessage = statusMessage;
        }
    }

    /// <summary>
    /// Robot 狀態更新事件參數
    /// </summary>
    public class RobotStatusUpdatedEventArgs : EventArgs
    {
        public Joint6 JointAngles { get; }
        public Pose6 Pose { get; }
        public DateTime Timestamp { get; }

        public RobotStatusUpdatedEventArgs(Joint6 jointAngles, Pose6 pose)
        {
            JointAngles = jointAngles;
            Pose = pose;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// PLC 數據讀取完成事件參數
    /// </summary>
    public class PlcDataReadEventArgs : EventArgs
    {
        public int Address { get; }
        public int Value { get; }
        public DateTime Timestamp { get; }

        public PlcDataReadEventArgs(int address, int value)
        {
            Address = address;
            Value = value;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// 異常發生事件參數
    /// </summary>
    public class ExceptionOccurredEventArgs : EventArgs
    {
        public Exception Exception { get; }
        string Source { get; }
        public DateTime Timestamp { get; }

        public ExceptionOccurredEventArgs(Exception exception, string source)
        {
            Exception = exception;
            Source = source;
            Timestamp = DateTime.Now;
        }
    }
}
