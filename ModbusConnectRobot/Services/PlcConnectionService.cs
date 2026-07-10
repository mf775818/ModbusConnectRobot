using System;
using System.Threading;
using System.Threading.Tasks;
using ModbusConnectRobot.Events;
using ModbusConnectRobot.Interfaces;

namespace ModbusConnectRobot.Services
{
    /// <summary>
    /// 工業級 PLC 連接服務 - 支援事件驅動重連機制
    /// </summary>
    public class PlcConnectionService : IPlcConnectionService
    {
        private readonly ProgramMacrofilters _macros;
        private readonly SynchronizationContext _uiContext;
        
        // 連接狀態
        private bool _isConnected;
        private string _plcIP;
        private int _socketID;
        
        // 重連機制
        private CancellationTokenSource _reconnectCts;
        private bool _autoReconnectEnabled;
        private int _maxReconnectAttempts = 5;
        private TimeSpan _retryDelay = TimeSpan.FromSeconds(2);
        private int _currentReconnectAttempt;
        
        // 健康檢查
        private Timer _healthCheckTimer;
        private readonly TimeSpan _healthCheckInterval = TimeSpan.FromSeconds(5);
        
        // 事件
        public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;
        public event EventHandler<DisconnectedEventArgs> Disconnected;

        public bool IsConnected => _isConnected;
        public string PLCIP => _plcIP;
        public int SocketID => _socketID;

        public PlcConnectionService(ProgramMacrofilters macros, SynchronizationContext uiContext = null)
        {
            _macros = macros ?? throw new ArgumentNullException(nameof(macros));
            _uiContext = uiContext ?? SynchronizationContext.Current;
            _isConnected = false;
            _socketID = -1;
        }

        public async Task<bool> ConnectAsync(string ip, int timeoutMilliseconds = 5000)
        {
            if (_isConnected)
            {
                throw new InvalidOperationException("Already connected. Disconnect first.");
            }

            _plcIP = ip;
            var connectTask = Task.Run(() =>
            {
                try
                {
                    _macros.ConnectToPLC(ip, out int socketID, out bool plcConnected);
                    if (plcConnected)
                    {
                        _socketID = socketID;
                    }
                    return plcConnected;
                }
                catch (Exception ex)
                {
                    OnExceptionOccurred(ex, "PlcConnectionService.ConnectAsync");
                    return false;
                }
            });

            var completedTask = await Task.WhenAny(connectTask, Task.Delay(timeoutMilliseconds));
            
            if (completedTask == connectTask)
            {
                bool result = await connectTask;
                if (result)
                {
                    SetConnectedState(true);
                    StartHealthCheck();
                    OnConnectionStateChanged(true);
                }
                return result;
            }
            else
            {
                OnConnectionStateChanged(false, "Connection timeout");
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            StopHealthCheck();
            DisableAutoReconnect();
            
            await Task.Run(() =>
            {
                try
                {
                    // PLC 斷開連接（如果有對應的方法）
                    // _macros.DisconnectPLC(_socketID);
                    _socketID = -1;
                }
                catch (Exception ex)
                {
                    OnExceptionOccurred(ex, "PlcConnectionService.DisconnectAsync");
                }
            });

            SetConnectedState(false);
            OnConnectionStateChanged(false);
        }

        public void EnableAutoReconnect(int maxAttempts = 5, TimeSpan? retryDelay = null)
        {
            _autoReconnectEnabled = true;
            _maxReconnectAttempts = maxAttempts;
            _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
        }

        public void DisableAutoReconnect()
        {
            _autoReconnectEnabled = false;
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = null;
        }

        private async void StartHealthCheck()
        {
            StopHealthCheck();
            
            _healthCheckTimer = new Timer(async state =>
            {
                if (!_isConnected) return;

                bool isHealthy = await HealthCheckAsync();
                if (!isHealthy)
                {
                    OnDisconnected("Health check failed");
                    
                    if (_autoReconnectEnabled)
                    {
                        _ = StartReconnectProcessAsync();
                    }
                }
            }, null, _healthCheckInterval, _healthCheckInterval);
        }

        private void StopHealthCheck()
        {
            _healthCheckTimer?.Dispose();
            _healthCheckTimer = null;
        }

        private async Task<bool> HealthCheckAsync()
        {
            if (!_isConnected || _socketID < 0) return false;

            try
            {
                // 嘗試讀取一個暫存器來檢查連接
                List<int> outIntegerValue = new List<int>();
                _macros.ReadPLC(_socketID, 0, outIntegerValue);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task StartReconnectProcessAsync()
        {
            if (_reconnectCts != null)
            {
                return; // Already reconnecting
            }

            _reconnectCts = new CancellationTokenSource();
            _currentReconnectAttempt = 0;

            while (_currentReconnectAttempt < _maxReconnectAttempts && 
                   !_reconnectCts.Token.IsCancellationRequested)
            {
                _currentReconnectAttempt++;

                OnReconnectProgress(
                    _currentReconnectAttempt,
                    _maxReconnectAttempts,
                    _retryDelay,
                    $"[PLC] Attempting reconnection ({_currentReconnectAttempt}/{_maxReconnectAttempts})..."
                );

                try
                {
                    bool success = await ConnectAsync(_plcIP, 5000);
                    if (success)
                    {
                        OnConnectionStateChanged(true);
                        _currentReconnectAttempt = 0;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    OnExceptionOccurred(ex, "PlcConnectionService.Reconnect");
                }

                if (_currentReconnectAttempt < _maxReconnectAttempts)
                {
                    await Task.Delay(_retryDelay, _reconnectCts.Token);
                }
            }

            // Max attempts reached
            SetConnectedState(false);
            OnConnectionStateChanged(false, $"[PLC] Max reconnection attempts ({_maxReconnectAttempts}) reached");
            _reconnectCts?.Dispose();
            _reconnectCts = null;
        }

        protected virtual void OnDisconnected(string reason)
        {
            var args = new DisconnectedEventArgs(reason, _autoReconnectEnabled);
            Disconnected?.Invoke(this, args);
        }

        protected virtual void OnConnectionStateChanged(bool isConnected, string errorMessage = null)
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(isConnected, errorMessage));
        }

        protected virtual void OnReconnectProgress(int attemptNumber, int maxAttempts, TimeSpan nextRetryDelay, string statusMessage)
        {
            // Can be extended to raise an event
        }

        protected virtual void OnExceptionOccurred(Exception exception, string source)
        {
            System.Diagnostics.Debug.WriteLine($"[{source}] Exception: {exception.Message}");
        }

        private void SetConnectedState(bool connected)
        {
            _isConnected = connected;
        }

        public void Dispose()
        {
            DisableAutoReconnect();
            StopHealthCheck();
        }
    }
}
