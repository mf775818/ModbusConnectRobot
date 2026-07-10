using System;
using System.Threading.Tasks;
using ModbusConnectRobot.Events;

namespace ModbusConnectRobot.Interfaces
{
    /// <summary>
    /// Robot 連接服務接口 - 工業級連接管理
    /// </summary>
    public interface IRobotConnectionService : IDisposable
    {
        /// <summary>
        /// 連接狀態改變事件
        /// </summary>
        event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

        /// <summary>
        /// 斷線事件 - 用於觸發重連流程
        /// </summary>
        event EventHandler<DisconnectedEventArgs> Disconnected;

        /// <summary>
        /// 重連進度事件
        /// </summary>
        event EventHandler<ReconnectProgressEventArgs> ReconnectProgress;

        /// <summary>
        /// 是否已連接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Robot IP 地址
        /// </summary>
        string RobotIP { get; }

        /// <summary>
        /// 連接到 Robot
        /// </summary>
        Task<bool> ConnectAsync(string ip, int timeoutMilliseconds = 5000);

        /// <summary>
        /// 斷開連接
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// 啟動自動重連機制
        /// </summary>
        void EnableAutoReconnect(int maxAttempts = 5, TimeSpan? retryDelay = null);

        /// <summary>
        /// 禁用自動重連
        /// </summary>
        void DisableAutoReconnect();

        /// <summary>
        /// 檢查連接健康狀態
        /// </summary>
        Task<bool> HealthCheckAsync();
    }

    /// <summary>
    /// PLC 連接服務接口
    /// </summary>
    public interface IPlcConnectionService : IDisposable
    {
        event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;
        event EventHandler<DisconnectedEventArgs> Disconnected;

        bool IsConnected { get; }
        string PLCIP { get; }
        int SocketID { get; }

        Task<bool> ConnectAsync(string ip, int timeoutMilliseconds = 5000);
        Task DisconnectAsync();
        void EnableAutoReconnect(int maxAttempts = 5, TimeSpan? retryDelay = null);
        void DisableAutoReconnect();
    }

    /// <summary>
    /// Robot 操作服務接口
    /// </summary>
    public interface IRobotOperationService
    {
        Task<bool> GetJointAnglesAsync(out Joint6 jointAngles);
        Task<bool> GetPoseAsync(out Pose6 pose);
        Task<bool> MoveJogStartAsync(JogType jogDirection);
        Task<bool> MoveJogStopAsync();
        Task<bool> MoveLAsync(int speed, int acceleration, Pose6 targetPose);
        Task<bool> StartDragModeAsync();
        Task<bool> StopDragModeAsync();
        Task<bool> SetSpeedFactorAsync(int speedFactor);
        Task ReleaseRobotAsync();
    }

    /// <summary>
    /// PLC 數據讀寫服務接口
    /// </summary>
    public interface IPlcDataService
    {
        Task WriteAsync(int socketID, int address, int value);
        Task<int> ReadAsync(int socketID, int address);
    }

    /// <summary>
    /// 視覺檢測服務接口
    /// </summary>
    public interface IVisionInspectionService : IDisposable
    {
        event EventHandler<VisionInspectionResultEventArgs> InspectionCompleted;

        Task StartContinuousInspectionAsync();
        Task StopInspectionAsync();
        bool IsInspecting { get; }
    }

    /// <summary>
    /// 視覺檢測結果事件參數
    /// </summary>
    public class VisionInspectionResultEventArgs : EventArgs
    {
        public Avl.Image GrabImage { get; }
        public int MoldNumber { get; }
        public Box DetectedObject { get; }
        public DateTime Timestamp { get; }

        public VisionInspectionResultEventArgs(Avl.Image grabImage, int moldNumber, Box detectedObject)
        {
            GrabImage = grabImage;
            MoldNumber = moldNumber;
            DetectedObject = detectedObject;
            Timestamp = DateTime.Now;
        }
    }
}
