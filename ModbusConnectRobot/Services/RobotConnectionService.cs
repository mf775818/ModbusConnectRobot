using System;
using System.Threading;
using System.Threading.Tasks;
using ModbusConnectRobot.Events;
using ModbusConnectRobot.Interfaces;

namespace ModbusConnectRobot.Services
{
    /// <summary>
    /// 工業級 Robot 連接服務 - 支援事件驅動重連機制
    /// </summary>
    public class RobotConnectionService : IRobotConnectionService
    {
        private readonly ProgramMacrofilters _macros;
        private readonly SynchronizationContext _uiContext;
        
        // 連接狀態
        private bool _isConnected;
        private string _robotIP;
        
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
        public event EventHandler<ReconnectProgressEventArgs> ReconnectProgress;

        public bool IsConnected => _isConnected;
        public string RobotIP => _robotIP;

        public RobotConnectionService(ProgramMacrofilters macros, SynchronizationContext uiContext = null)
        {
            _macros = macros ?? throw new ArgumentNullException(nameof(macros));
            _uiContext = uiContext ?? SynchronizationContext.Current;
            _isConnected = false;
        }

        public async Task<bool> ConnectAsync(string ip, int timeoutMilliseconds = 5000)
        {
            if (_isConnected)
            {
                throw new InvalidOperationException("Already connected. Disconnect first.");
            }

            _robotIP = ip;
            var connectTask = Task.Run(() =>
            {
                try
                {
                    _macros.ConnectToRobot(ip, out bool robotConnected);
                    return robotConnected;
                }
                catch (Exception ex)
                {
                    OnExceptionOccurred(ex, "RobotConnectionService.ConnectAsync");
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
                    _macros.ReleaseRobot(out bool result);
                    if (!result)
                    {
                        throw new Exception("Failed to release robot connection");
                    }
                }
                catch (Exception ex)
                {
                    OnExceptionOccurred(ex, "RobotConnectionService.DisconnectAsync");
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

        public async Task<bool> HealthCheckAsync()
        {
            if (!_isConnected) return false;

            try
            {
                Conditional<Joint6> outJointAngles = new Conditional<Joint6>();
                Conditional<string> outResponseString = new Conditional<string>();
                Conditional<List<string>> outResponseStringArray = new Conditional<List<string>>();
                
                _macros.GetAngle(out Conditional<bool> outResult, outResponseString, outResponseStringArray, outJointAngles);
                
                return outResult.Value;
            }
            catch
            {
                return false;
            }
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
                    $"Attempting reconnection ({_currentReconnectAttempt}/{_maxReconnectAttempts})..."
                );

                try
                {
                    bool success = await ConnectAsync(_robotIP, 5000);
                    if (success)
                    {
                        OnConnectionStateChanged(true);
                        _currentReconnectAttempt = 0;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    OnExceptionOccurred(ex, "RobotConnectionService.Reconnect");
                }

                if (_currentReconnectAttempt < _maxReconnectAttempts)
                {
                    await Task.Delay(_retryDelay, _reconnectCts.Token);
                }
            }

            // Max attempts reached
            SetConnectedState(false);
            OnConnectionStateChanged(false, $"Max reconnection attempts ({_maxReconnectAttempts}) reached");
            _reconnectCts?.Dispose();
            _reconnectCts = null;
        }

        private void OnDisconnected(string reason)
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
            ReconnectProgress?.Invoke(this, new ReconnectProgressEventArgs(attemptNumber, maxAttempts, nextRetryDelay, statusMessage));
        }

        protected virtual void OnExceptionOccurred(Exception exception, string source)
        {
            // Can be extended to raise an event
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
