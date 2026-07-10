using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AuroraVision;
using Avl;
using HMI.Controls;
using ModbusConnectRobot.Events;
using ModbusConnectRobot.Interfaces;
using ModbusConnectRobot.Services;

namespace ModbusConnectRobot
{
    /// <summary>
    /// 工業級 Robot 控制面板 - 經過破壞式重構的完整版本
    /// 特點：
    /// 1. 事件驅動的連接管理
    /// 2. 自動重連機制
    /// 3. 服務層與 UI 層解耦
    /// 4. 異步操作避免 UI 阻塞
    /// 5. 完整的錯誤處理
    /// 6. 數位孿生支援 - 完整狀態管理與旗標轉換
    /// </summary>
    public partial class Form1 : Form
    {
        // ===== 服務層組件 =====
        private IRobotConnectionService _robotConnectionService;
        private IPlcConnectionService _plcConnectionService;
        private IRobotOperationService _robotOperationService;
        
        // ===== 系統狀態管理 (數位孿生核心) =====
        private EquipmentStatus _eqStatus = EquipmentStatus.Eq01_NotInitialed;
        private bool _isDraggingActive;
        private bool _isJogging;
        private bool _isAutoRunning;
        private bool _isScanning;
        private bool _isRobotConnected;
        private bool _isPlcConnected;
        private int _currentSpeedFactor = 10;
        private CancellationTokenSource _autoRunCts;
        private System.Threading.Timer _backgroundTimer;
        
        // ===== 數據存儲 =====
        private readonly List<Pose6> _taughtPoints = new List<Pose6>();
        private int _pointIndex = 1;
        private string _pointName = "";
        
        // ===== UI 組件 =====
        private readonly View2DBox _viewer2D = new View2DBox();
        private Image _dragActiveImage;
        private Image _dragInactiveImage;
        
        // ===== 配置與宏 =====
        private readonly string _avsProjectPath = @"C:\TK_CC\AuroraVision 影像系統\TK_Sandbox\ModbusConnectAll_MarcoFilter\Program.avproj";
        private ProgramMacrofilters _macros;
        
        // ===== Conditional 變數 (與 AuroraVision 溝通) =====
        private Conditional<bool> _outResult;
        private Conditional<string> _outResponseString;
        private Conditional<List<string>> _outResponseStringArray;
        private int _outSocketID;

        /// <summary>
        /// 設備狀態列舉 - 用於數位孿生狀態追蹤
        /// </summary>
        public enum EquipmentStatus : int
        {
            Eq01_NotInitialed = 0,  // 尚未初始化
            Eq02_Initializing = 1,  // 初始化中
            Eq03_Idle = 2,          // 待命中
            Eq04_Preparing = 3,     // 準備中
            Eq05_AutoRunning = 4,   // 自動運行中
            Eq06_CloseSystem = 5,   // 系統關閉
        }

        public Form1()
        {
            InitializeComponent();
            InitializeServices();
            InitializeUI();
            SubscribeToEvents();
            InitializeConditionalVariables();
        }

        /// <summary>
        /// 初始化 Conditional 變數 - 與 AuroraVision 溝通所需
        /// </summary>
        private void InitializeConditionalVariables()
        {
            _outResult = new Conditional<bool>();
            _outResponseString = new Conditional<string>();
            _outResponseStringArray = new Conditional<List<string>>();
        }

        /// <summary>
        /// 初始化所有服務 - 依賴注入的核心
        /// </summary>
        private void InitializeServices()
        {
            try
            {
                _macros = new ProgramMacrofilters(_avsProjectPath);
                
                // 創建服務實例
                _robotConnectionService = new RobotConnectionService(_macros, SynchronizationContext.Current);
                _plcConnectionService = new PlcConnectionService(_macros, SynchronizationContext.Current);
                _robotOperationService = new RobotOperationService(_macros, SynchronizationContext.Current);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化服務失敗：{ex.Message}", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 初始化 UI 組件
        /// </summary>
        private void InitializeUI()
        {
            ApplyStyle();
            SetupDataGridView();
            SetControlEnabled(false);

            // 初始化 Viewer
            _viewer2D.Parent = panel1;
            _viewer2D.Dock = DockStyle.Fill;
            _viewer2D.SizeMode = VideoBoxBase.ZoomingVideoBoxMode.Fit;
            _viewer2D.InitialSizeMode = ZoomingVideoBoxSizeMode.FitToWindow;
            _viewer2D.Primitives.Add();

            // 載入拖曳按鈕圖片
            try
            {
                _dragActiveImage = Image.FromFile(@"C:\TK_CC\AuroraVision 影像系統\TK_Sandbox\CSharpCC\ModbusConnectRobot\DragOn.jpg");
                _dragInactiveImage = Image.FromFile(@"C:\TK_CC\AuroraVision 影像系統\TK_Sandbox\CSharpCC\ModbusConnectRobot\DragOff.jpg");
                _dragActiveImage = ResizeImage(_dragActiveImage, DragRobotbtn.Width, DragRobotbtn.Height);
                _dragInactiveImage = ResizeImage(_dragInactiveImage, DragRobotbtn.Width, DragRobotbtn.Height);
                DragRobotbtn.Image = _dragInactiveImage;
                DragRobotbtn.ImageAlign = ContentAlignment.MiddleCenter;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"載入圖片失敗：{ex.Message}", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 訂閱所有服務事件 - 事件驅動架構的核心
        /// </summary>
        private void SubscribeToEvents()
        {
            // Robot 連接事件
            if (_robotConnectionService != null)
            {
                _robotConnectionService.ConnectionStateChanged += OnRobotConnectionStateChanged;
                _robotConnectionService.Disconnected += OnRobotDisconnected;
                _robotConnectionService.ReconnectProgress += OnReconnectProgress;
            }

            // PLC 連接事件
            if (_plcConnectionService != null)
            {
                _plcConnectionService.ConnectionStateChanged += OnPlcConnectionStateChanged;
                _plcConnectionService.Disconnected += OnPlcDisconnected;
            }
        }

        #region 事件處理器

        /// <summary>
        /// Robot 連接狀態改變事件處理器
        /// </summary>
        private void OnRobotConnectionStateChanged(object sender, ConnectionStateChangedEventArgs e)
        {
            InvokeIfNeeded(() =>
            {
                if (e.IsConnected)
                {
                    textBox2.Text = "Robot 已成功連線";
                    Connectbtn.BackColor = Color.FromArgb(115, 191, 57);
                    
                    // 檢查是否兩者都已連接
                    if (_plcConnectionService?.IsConnected == true)
                    {
                        ChangeEquipmentStatus(EquipmentStatus.Eq03_Idle);
                        SetControlEnabled(true);
                        SetJogSpeed(10);
                    }
                }
                else
                {
                    textBox2.Text = $"Robot 斷線：{e.ErrorMessage}";
                    Connectbtn.BackColor = SystemColors.Control;
                    SetControlEnabled(false);
                }
            });
        }

        /// <summary>
        /// Robot 斷線事件處理器 - 觸發自動重連
        /// </summary>
        private void OnRobotDisconnected(object sender, DisconnectedEventArgs e)
        {
            InvokeIfNeeded(() =>
            {
                textBox2.Text = $"Robot 斷線：{e.Reason}";
                
                if (e.ShouldAutoReconnect)
                {
                    textBox2.Text += " - 正在嘗試自動重連...";
                }
            });
        }

        /// <summary>
        /// PLC 連接狀態改變事件處理器
        /// </summary>
        private void OnPlcConnectionStateChanged(object sender, ConnectionStateChangedEventArgs e)
        {
            InvokeIfNeeded(() =>
            {
                if (e.IsConnected)
                {
                    textBox2.Text = "PLC 已成功連線";
                    
                    // 檢查是否兩者都已連接
                    if (_robotConnectionService?.IsConnected == true)
                    {
                        ChangeEquipmentStatus(EquipmentStatus.Eq03_Idle);
                        SetControlEnabled(true);
                        SetJogSpeed(10);
                    }
                }
                else
                {
                    textBox2.Text = $"PLC 斷線：{e.ErrorMessage}";
                    SetControlEnabled(false);
                }
            });
        }

        /// <summary>
        /// PLC 斷線事件處理器
        /// </summary>
        private void OnPlcDisconnected(object sender, DisconnectedEventArgs e)
        {
            InvokeIfNeeded(() =>
            {
                textBox2.Text = $"PLC 斷線：{e.Reason}";
                
                if (e.ShouldAutoReconnect)
                {
                    textBox2.Text += " - 正在嘗試自動重連...";
                }
            });
        }

        /// <summary>
        /// 重連進度事件處理器
        /// </summary>
        private void OnReconnectProgress(object sender, ReconnectProgressEventArgs e)
        {
            InvokeIfNeeded(() =>
            {
                textBox2.Text = e.StatusMessage;
            });
        }

        #endregion

        #region 連接控制

        /// <summary>
        /// 連接按鈕點擊事件 - 異步連接流程
        /// </summary>
        private async void Connectbtn_Click(object sender, EventArgs e)
        {
            if (_eqStatus >= EquipmentStatus.Eq02_Initializing)
            {
                MessageBox.Show("目前系統正在初始化或已連線，請稍後再試。", "無法連線", 
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string robotIP = textBox1.Text;
            string plcIP = textBox17.Text;
            
            ChangeEquipmentStatus(EquipmentStatus.Eq02_Initializing);
            textBox2.Text = "連線中，請稍候...";
            Connectbtn.Enabled = false;

            try
            {
                // 啟用自動重連（工業級需求）
                _robotConnectionService.EnableAutoReconnect(maxAttempts: 5, retryDelay: TimeSpan.FromSeconds(2));
                _plcConnectionService.EnableAutoReconnect(maxAttempts: 5, retryDelay: TimeSpan.FromSeconds(2));

                // 並行連接 Robot 和 PLC
                var connectTasks = new[]
                {
                    _robotConnectionService.ConnectAsync(robotIP, 5000),
                    _plcConnectionService.ConnectAsync(plcIP, 5000)
                };

                var results = await Task.WhenAll(connectTasks);
                bool robotConnected = results[0];
                bool plcConnected = results[1];

                if (robotConnected && plcConnected)
                {
                    textBox2.Text = "Robot 與 PLC 已成功連線。";
                    Connectbtn.BackColor = Color.FromArgb(115, 191, 57);
                    ChangeEquipmentStatus(EquipmentStatus.Eq03_Idle);
                    SetControlEnabled(true);
                    SetJogSpeed(10);
                }
                else
                {
                    string failMsg = "連線失敗：";
                    if (!robotConnected) failMsg += "Robot ";
                    if (!plcConnected) failMsg += (!robotConnected ? "與 " : "") + "PLC ";
                    failMsg += "無法連線，請檢查設定與網路。";

                    MessageBox.Show(failMsg, "連線失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    textBox2.Text = failMsg;
                    SetControlEnabled(false);
                    ChangeEquipmentStatus(EquipmentStatus.Eq01_NotInitialed);
                }
            }
            catch (TimeoutException)
            {
                MessageBox.Show("連線逾時，請確認設備是否正常。", "連線逾時", 
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                textBox2.Text = "連線逾時...";
                ChangeEquipmentStatus(EquipmentStatus.Eq01_NotInitialed);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"連線時發生錯誤：{ex.Message}", "連線錯誤", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                textBox2.Text = "連線錯誤：" + ex.Message;
                ChangeEquipmentStatus(EquipmentStatus.Eq01_NotInitialed);
            }
            finally
            {
                Connectbtn.Enabled = true;
            }
        }

        /// <summary>
        /// 關閉系統按鈕 - 優雅斷開所有連接
        /// </summary>
        private async void CloseSysbtn_Click(object sender, EventArgs e)
        {
            ChangeEquipmentStatus(EquipmentStatus.Eq06_CloseSystem);
            
            try
            {
                // 停止所有自動任務
                _autoRunCts?.Cancel();
                _isAutoRunning = false;

                // 斷開連接
                if (_robotConnectionService != null)
                {
                    await _robotConnectionService.DisconnectAsync();
                }

                if (_plcConnectionService != null)
                {
                    await _plcConnectionService.DisconnectAsync();
                }

                textBox2.Text = "系統已關閉";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"關閉時發生錯誤：{ex.Message}", "錯誤", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                this.Close();
            }
        }

        #endregion

        #region Robot 操作

        /// <summary>
        /// Jog 運動開始 - 使用異步操作
        /// </summary>
        private async void XPosDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            if (!_isJogging)
            {
                _isJogging = true;
                bool success = await _robotOperationService.MoveJogStartAsync(JogType.X_Positive);
                textBox2.Text = success ? "Jogging X+" : "Failed to start Jog movement.";
            }
        }

        /// <summary>
        /// Jog 運動停止
        /// </summary>
        private async void XPosDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            if (_isJogging)
            {
                _isJogging = false;
                bool success = await _robotOperationService.MoveJogStopAsync();
                textBox2.Text = success ? "Jog movement stopped." : "Failed to stop Jog movement.";
            }
        }

        // 其他方向按鈕的事件處理器（略，模式相同）
        private async void XNegDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            if (!_isJogging)
            {
                _isJogging = true;
                bool success = await _robotOperationService.MoveJogStartAsync(JogType.X_Negtive);
                textBox2.Text = success ? "Jogging X-" : "Failed to start Jog movement.";
            }
        }

        private async void XNegDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            if (_isJogging)
            {
                _isJogging = false;
                bool success = await _robotOperationService.MoveJogStopAsync();
                textBox2.Text = success ? "Jog movement stopped." : "Failed to stop Jog movement.";
            }
        }

        private async void YPosDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            _isJogging = true;
            bool success = await _robotOperationService.MoveJogStartAsync(JogType.Y_Positive);
            textBox2.Text = success ? "Jogging Y+" : "Failed to start Jog movement.";
        }

        private async void YPosDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            _isJogging = false;
            bool success = await _robotOperationService.MoveJogStopAsync();
            textBox2.Text = success ? "Jog movement stopped." : "Failed to stop Jog movement.";
        }

        private async void YNegDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            _isJogging = true;
            bool success = await _robotOperationService.MoveJogStartAsync(JogType.Y_Negtive);
            textBox2.Text = success ? "Jogging Y-" : "Failed to start Jog movement.";
        }

        private async void YNegDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            _isJogging = false;
            bool success = await _robotOperationService.MoveJogStopAsync();
            textBox2.Text = success ? "Jog movement stopped." : "Failed to stop Jog movement.";
        }

        private async void ZPosDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            if (!_isJogging)
            {
                _isJogging = true;
                bool success = await _robotOperationService.MoveJogStartAsync(JogType.Z_Positive);
                textBox2.Text = success ? "Jogging Z+" : "Failed to start Jog movement.";
            }
        }

        private async void ZPosDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            if (_isJogging)
            {
                _isJogging = false;
                bool success = await _robotOperationService.MoveJogStopAsync();
                textBox2.Text = success ? "Jog movement stopped." : "Failed to stop Jog movement.";
            }
        }

        private async void ZNegDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            if (!_isJogging)
            {
                _isJogging = true;
                bool success = await _robotOperationService.MoveJogStartAsync(JogType.Z_Negtive);
                textBox2.Text = success ? "Jogging Z-" : "Failed to start Jog movement.";
            }
        }

        private async void ZNegDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            if (_isJogging)
            {
                _isJogging = false;
                bool success = await _robotOperationService.MoveJogStopAsync();
                textBox2.Text = success ? "Jog movement stopped." : "Failed to stop Jog movement.";
            }
        }

        /// <summary>
        /// 設定 Jog 速度
        /// </summary>
        private async void SetupVelbtn_Click(object sender, EventArgs e)
        {
            if (int.TryParse(textBox16.Text, out int newSpeed))
            {
                if (newSpeed < 0 || newSpeed > 100)
                {
                    textBox2.Text = "Speed must be between 0 and 100.";
                    return;
                }
                
                bool success = await _robotOperationService.SetSpeedFactorAsync(newSpeed);
                if (success)
                {
                    _currentSpeedFactor = newSpeed;
                    textBox2.Text = $"Speed set to {newSpeed}%.";
                }
                else
                {
                    textBox2.Text = "Failed to set speed factor.";
                }
            }
            else
            {
                textBox2.Text = "Invalid speed input.";
            }
        }

        /// <summary>
        /// 拖曳模式開關
        /// </summary>
        private async void DragRobotbtn_Click(object sender, EventArgs e)
        {
            if (_isDraggingActive)
            {
                bool success = await _robotOperationService.StopDragModeAsync();
                if (success)
                {
                    _isDraggingActive = false;
                    DragRobotbtn.Image = _dragInactiveImage;
                    textBox2.Text = "Drag Mode Deactivated.";
                }
            }
            else
            {
                bool success = await _robotOperationService.StartDragModeAsync();
                if (success)
                {
                    _isDraggingActive = true;
                    DragRobotbtn.Image = _dragActiveImage;
                    textBox2.Text = "Drag Mode Activated.";
                }
            }
        }

        #endregion

        #region 教點功能

        /// <summary>
        /// 教點 - 記錄當前位置
        /// </summary>
        private async void Teachbtn_Click(object sender, EventArgs e)
        {
            bool success = await _robotOperationService.GetPoseAsync(out Pose6 currentPose);
            
            if (success)
            {
                _taughtPoints.Add(currentPose);
                string pointName = $"P{_pointIndex++}";

                dataGridView1.Rows.Add(
                    pointName,
                    currentPose.X,
                    currentPose.Y,
                    currentPose.Z,
                    currentPose.Rx,
                    currentPose.Ry,
                    currentPose.Rz
                );
            }
            else
            {
                MessageBox.Show("無法取得目前位置，請再試一次。", "錯誤", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 播放所有點位
        /// </summary>
        private async void Playbtn_Click(object sender, EventArgs e)
        {
            if (_taughtPoints.Count == 0)
            {
                MessageBox.Show("尚未記錄任何點位！", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            for (int i = 0; i < _taughtPoints.Count; i++)
            {
                var point = _taughtPoints[i];
                string pointName = dataGridView1.Rows[i].Cells[0].Value.ToString();

                bool success = await MoveToPointAsync(point);

                if (!success)
                {
                    MessageBox.Show($"點位 {pointName} 移動失敗！", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                await Task.Delay(500);
            }
        }

        /// <summary>
        /// 移動到指定點位
        /// </summary>
        private async Task<bool> MoveToPointAsync(Pose6 targetPose, bool isAutoRun = false)
        {
            const int speed = 20;
            const int acceleration = 100;

            try
            {
                bool success = await _robotOperationService.MoveLAsync(speed, acceleration, targetPose);

                if (success)
                {
                    InvokeIfNeeded(() =>
                    {
                        textBox2.Text = isAutoRun
                            ? "AutoRun 執行中"
                            : $"移動成功：X={targetPose.X}, Y={targetPose.Y}, Z={targetPose.Z}";
                    });
                    return true;
                }
                else
                {
                    InvokeIfNeeded(() =>
                    {
                        textBox2.Text = "移動失敗";
                    });
                    return false;
                }
            }
            catch (Exception ex)
            {
                InvokeIfNeeded(() =>
                {
                    textBox2.Text = $"發生錯誤：{ex.Message}";
                });
                return false;
            }
        }

        /// <summary>
        /// 刪除選中點位
        /// </summary>
        private void DeletePointbtn_Click(object sender, EventArgs e)
        {
            if (dataGridView1.SelectedRows.Count > 0)
            {
                int index = dataGridView1.SelectedRows[0].Index;
                _taughtPoints.RemoveAt(index);
                dataGridView1.Rows.RemoveAt(index);
            }
            else
            {
                MessageBox.Show("請選擇要刪除的點位。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 重新編號點位
        /// </summary>
        private void ReindexPointbtn_Click(object sender, EventArgs e)
        {
            _pointIndex = 1;

            for (int i = 0; i < dataGridView1.Rows.Count; i++)
            {
                if (dataGridView1.Rows[i].Cells[0].Value != null)
                {
                    string pointName = $"P{_pointIndex}";
                    dataGridView1.Rows[i].Cells[0].Value = pointName;
                    _pointIndex++;
                }
            }
        }

        /// <summary>
        /// 插入點位
        /// </summary>
        private async void InsertPointbtn_Click(object sender, EventArgs e)
        {
            if (dataGridView1.SelectedRows.Count == 0)
            {
                MessageBox.Show("請選擇要插入點位的位置。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int insertIndex = dataGridView1.SelectedRows[0].Index;

            bool success = await _robotOperationService.GetPoseAsync(out Pose6 currentPose);

            if (success)
            {
                _taughtPoints.Insert(insertIndex + 1, currentPose);
                dataGridView1.Rows.Insert(insertIndex + 1, "",
                    currentPose.X, currentPose.Y, currentPose.Z,
                    currentPose.Rx, currentPose.Ry, currentPose.Rz);
            }
            else
            {
                MessageBox.Show("無法取得目前位置，請再試一次。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 移動到選中點位
        /// </summary>
        private async void MoveToPointbtn_Click(object sender, EventArgs e)
        {
            if (dataGridView1.SelectedRows.Count == 0)
            {
                MessageBox.Show("請選擇要移動的點位。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int selectedIndex = dataGridView1.SelectedRows[0].Index;
            var targetPoint = _taughtPoints[selectedIndex];
            string pointName = dataGridView1.Rows[selectedIndex].Cells[0].Value.ToString();

            bool success = await MoveToPointAsync(targetPoint);

            if (success)
            {
                MessageBox.Show($"已成功移動到點位 {pointName}。", "完成", 
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show($"移動到點位 {pointName} 失敗！", "錯誤", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region 點位儲存/載入

        /// <summary>
        /// 儲存點位到 JSON 檔案
        /// </summary>
        private async void SavePointbtn_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Filter = "JSON Files (*.json)|*.json";
                saveFileDialog.Title = "儲存點位檔案";
                saveFileDialog.InitialDirectory = @"C:\";

                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var pointsData = _taughtPoints.Select((pose, index) => new
                        {
                            Name = $"P{index + 1}",
                            X = pose.X,
                            Y = pose.Y,
                            Z = pose.Z,
                            Rx = pose.Rx,
                            Ry = pose.Ry,
                            Rz = pose.Rz
                        }).ToList();

                        string json = await Task.Run(() =>
                            JsonSerializer.Serialize(pointsData, new JsonSerializerOptions { WriteIndented = true }));

                        File.WriteAllText(saveFileDialog.FileName, json);
                        MessageBox.Show("點位已成功儲存！", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"儲存點位時發生錯誤：{ex.Message}", "錯誤", 
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        /// <summary>
        /// 從 JSON 檔案載入點位
        /// </summary>
        private async void LoadPointbtn_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "JSON Files (*.json)|*.json";
                openFileDialog.Title = "載入點位檔案";
                openFileDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string filePath = openFileDialog.FileName;
                        string json = await Task.Run(() => File.ReadAllText(filePath));
                        var pointsData = JsonSerializer.Deserialize<List<Pose6>>(json);

                        if (pointsData != null)
                        {
                            _taughtPoints.Clear();
                            _taughtPoints.AddRange(pointsData);

                            string fileName = Path.GetFileNameWithoutExtension(filePath);
                            InvokeIfNeeded(() =>
                            {
                                UpdateDataGridView(fileName);
                                MessageBox.Show("點位已成功載入！", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            });
                        }
                        else
                        {
                            MessageBox.Show("檔案內容格式錯誤。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        InvokeIfNeeded(() =>
                        {
                            MessageBox.Show($"讀取檔案時發生錯誤：{ex.Message}", "錯誤", 
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                        });
                    }
                }
            }
        }

        #endregion

        #region PLC 操作

        /// <summary>
        /// 寫入 PLC
        /// </summary>
        private void WritePLCbtn_Click(object sender, EventArgs e)
        {
            try
            {
                int regAddress = int.Parse(WriteAdsTB.Text);
                int writeValue = int.Parse(WriteValueTB.Text);
                
                if (_plcConnectionService is PlcConnectionService plcService)
                {
                    _macros.WritePLC(plcService.SocketID, regAddress, writeValue);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"寫入 PLC 錯誤：{ex.Message}", "錯誤", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 讀取 PLC
        /// </summary>
        private void ReadPLCbtn_Click(object sender, EventArgs e)
        {
            try
            {
                int startingAddress = int.Parse(ReadAdsTB.Text);
                List<int> outIntegerValue = new List<int>();
                
                if (_plcConnectionService is PlcConnectionService plcService)
                {
                    _macros.ReadPLC(plcService.SocketID, startingAddress, outIntegerValue);
                    ReadResultTB.Text = outIntegerValue[0].ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"讀取 PLC 錯誤：{ex.Message}", "錯誤", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region 自動運行

        /// <summary>
        /// 自動運行開關
        /// </summary>
        private void AutoRunbtn_Click(object sender, EventArgs e)
        {
            if (!_isAutoRunning)
            {
                _isAutoRunning = true;
                AutoRunbtn.BackColor = Color.Red;
                textBox2.Text = "AutoRun 已啟動...";
                _autoRunCts = new CancellationTokenSource();
                RunAutoCycleLoop(_autoRunCts.Token);
            }
            else
            {
                _autoRunCts?.Cancel();
                _isAutoRunning = false;
                AutoRunbtn.BackColor = Color.FromArgb(1, 104, 183);
                textBox2.Text = "AutoRun 已停止。";
            }
        }

        /// <summary>
        /// 自動運行循環
        /// </summary>
        private async void RunAutoCycleLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // 視覺檢測邏輯（保持原有實現）
                        int moldNum = -1;
                        Avl.Image grabImage = new Avl.Image();

                        _macros.Grab(grabImage);
                        _macros.Inspection(grabImage, out moldNum, out Box outObject);

                        InvokeIfNeeded(() =>
                        {
                            MoldNumTB.Text = moldNum.ToString();
                            textBox2.Text = moldNum <= 0 
                                ? "未識別到 QR code，重試中..." 
                                : $"識別成功：模具 {moldNum}";
                        });

                        if (moldNum > 0)
                        {
                            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                            string jsonPath = Path.Combine(desktopPath, $"MoldPath_{moldNum}.json");

                            if (!File.Exists(jsonPath))
                            {
                                InvokeIfNeeded(() =>
                                {
                                    MessageBox.Show($"找不到點位檔案：{jsonPath}");
                                });
                            }
                            else
                            {
                                string json = File.ReadAllText(jsonPath);
                                var pointsData = JsonSerializer.Deserialize<List<Pose6>>(json);

                                if (pointsData != null && pointsData.Count > 0)
                                {
                                    foreach (var point in pointsData)
                                    {
                                        bool success = await MoveToPointAsync(point, true);
                                        if (!success || token.IsCancellationRequested)
                                        {
                                            InvokeIfNeeded(() =>
                                            {
                                                textBox2.Text = "移動失敗或中斷，自動流程中止";
                                            });
                                            return;
                                        }
                                        await Task.Delay(500);
                                    }

                                    InvokeIfNeeded(() =>
                                    {
                                        textBox2.Text = $"模具 {moldNum} 執行完畢，等待下次 QR code...";
                                    });
                                }
                            }
                        }

                        await Task.Delay(1000);
                    }
                    catch (Exception ex)
                    {
                        InvokeIfNeeded(() =>
                        {
                            textBox2.Text = $"AutoRun 錯誤：{ex.Message}";
                        });
                        await Task.Delay(1000);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不需處理
            }
        }

        #endregion

        #region 輔助方法

        /// <summary>
        /// 變更設備狀態
        /// </summary>
        private void ChangeEquipmentStatus(EquipmentStatus status)
        {
            _eqStatus = status;
        }

        /// <summary>
        /// 設定控制項啟用狀態
        /// </summary>
        private void SetControlEnabled(bool enabled)
        {
            var controlButtons = new Button[]
            {
                CloseSysbtn,
                Teachbtn, Playbtn, DeletePointbtn, ReindexPointbtn,
                InsertPointbtn, MoveToPointbtn, SavePointbtn, LoadPointbtn,
                WritePLCbtn, ReadPLCbtn, SetupVelbtn,
                XPosDirbtn, XNegDirbtn, YPosDirbtn, YNegDirbtn,
                ZPosDirbtn, ZNegDirbtn, DragRobotbtn, AutoRunbtn
            };

            foreach (var btn in controlButtons)
            {
                btn.Enabled = enabled;
            }
        }

        /// <summary>
        /// 設定 DataGridView
        /// </summary>
        private void SetupDataGridView()
        {
            dataGridView1.ColumnCount = 7;
            dataGridView1.Columns[0].Name = "點位名稱";
            dataGridView1.Columns[1].Name = "X";
            dataGridView1.Columns[2].Name = "Y";
            dataGridView1.Columns[3].Name = "Z";
            dataGridView1.Columns[4].Name = "Rx";
            dataGridView1.Columns[5].Name = "Ry";
            dataGridView1.Columns[6].Name = "Rz";
        }

        /// <summary>
        /// 更新 DataGridView 顯示
        /// </summary>
        private void UpdateDataGridView(string fileName = "")
        {
            dataGridView1.Rows.Clear();
            _pointIndex = 1;

            DataGridViewCellStyle headerStyle = new DataGridViewCellStyle
            {
                Font = new Font("Microsoft YaHei UI", 8, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(115, 191, 57),
                Alignment = DataGridViewContentAlignment.MiddleCenter
            };

            if (!string.IsNullOrEmpty(fileName))
            {
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                dataGridView1.Columns[0].HeaderText = $"PathName : {fileNameWithoutExtension}";
            }
            else
            {
                dataGridView1.Columns[0].HeaderText = "PathName";
            }

            foreach (DataGridViewColumn column in dataGridView1.Columns)
            {
                column.HeaderCell.Style = headerStyle;
            }

            foreach (var point in _taughtPoints)
            {
                string pointName = $"P{_pointIndex}";
                dataGridView1.Rows.Add(pointName, point.X, point.Y, point.Z, point.Rx, point.Ry, point.Rz);
                _pointIndex++;
            }
        }

        /// <summary>
        /// 調整圖片大小
        /// </summary>
        private Image ResizeImage(Image image, int width, int height)
        {
            Bitmap resizedImage = new Bitmap(width, height);
            using (Graphics graphics = Graphics.FromImage(resizedImage))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, 0, 0, width, height);
            }
            return resizedImage;
        }

        /// <summary>
        /// 套用 UI 樣式
        /// </summary>
        private void ApplyStyle()
        {
            string fontName = "Microsoft YaHei UI";
            Font buttonFont = new Font(fontName, 12F, FontStyle.Bold);
            Font textFont = new Font(fontName, 12F, FontStyle.Bold);
            Color softBlueGray = Color.FromArgb(60, 60, 60);
            Color buttonGrayBlue = Color.FromArgb(85, 85, 85);
            Color whiteText = Color.White;
            Color greenHighlight = Color.FromArgb(115, 191, 57);

            this.BackColor = softBlueGray;

            void StyleButton(Button btn)
            {
                btn.Font = buttonFont;
                btn.BackColor = buttonGrayBlue;
                btn.ForeColor = whiteText;
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderSize = 0;
            }

            void StyleTextBox(TextBox tb)
            {
                tb.Font = textFont;
                tb.BackColor = Color.White;
                tb.ForeColor = Color.Black;
                tb.BorderStyle = BorderStyle.FixedSingle;
                tb.TextAlign = HorizontalAlignment.Center;
            }

            Button[] allButtons = {
                Connectbtn, CloseSysbtn, XPosDirbtn, XNegDirbtn, YPosDirbtn, YNegDirbtn,
                ZPosDirbtn, ZNegDirbtn, SetupVelbtn, AutoRunbtn, DragRobotbtn, Teachbtn, Playbtn,
                DeletePointbtn, ReindexPointbtn, InsertPointbtn, SavePointbtn, LoadPointbtn,
                WritePLCbtn, ReadPLCbtn, MoveToPointbtn
            };

            foreach (var btn in allButtons)
                StyleButton(btn);

            TextBox[] allTextBoxes = {
                textBox1, textBox17, textBox2, textBox3, textBox4, textBox5,
                textBox6, textBox7, textBox8, textBox9, textBox10, textBox11,
                textBox12, textBox13, textBox14, textBox15, textBox16,
                ReadAdsTB, ReadResultTB, WriteAdsTB, WriteValueTB, MoldNumTB
            };

            foreach (var tb in allTextBoxes)
                StyleTextBox(tb);

            textBox2.BackColor = Color.White;
            textBox2.ForeColor = Color.DarkSlateGray;

            dataGridView1.DefaultCellStyle.Font = new Font(fontName, 10, FontStyle.Regular);
            dataGridView1.ColumnHeadersDefaultCellStyle.Font = new Font(fontName, 10, FontStyle.Bold);
            dataGridView1.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            dataGridView1.ColumnHeadersDefaultCellStyle.BackColor = greenHighlight;
            dataGridView1.EnableHeadersVisualStyles = false;
            dataGridView1.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            dataGridView1.RowTemplate.Height = 28;

            tabControl1.Font = new Font(fontName, 10F, FontStyle.Bold);
            tabControl1.BackColor = softBlueGray;
        }

        /// <summary>
        /// 在需要時切換到 UI 執行緒
        /// </summary>
        private void InvokeIfNeeded(Action action)
        {
            if (InvokeRequired)
            {
                Invoke(action);
            }
            else
            {
                action();
            }
        }

        #endregion

        #region 表單生命週期

        /// <summary>
        /// 表單載入事件 - 初始化狀態監控計時器
        /// </summary>
        private void Form1_Load(object sender, EventArgs e)
        {
            // 服務已在建構式中初始化
            // 啟動背景狀態監控計時器（用於數位孿生狀態同步）
            _backgroundTimer = new System.Threading.Timer(UpdateRobotStatusThread, null, 0, 500);
        }

        /// <summary>
        /// 表單關閉事件 - 資源清理
        /// </summary>
        private async void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                // 停止背景計時器
                _backgroundTimer?.Dispose();
                
                // 停止所有自動任務
                _autoRunCts?.Cancel();
                
                // 停止掃描
                _isScanning = false;
                
                // 斷開連接並釋放資源
                if (_robotConnectionService != null)
                {
                    await _robotConnectionService.DisconnectAsync();
                    _robotConnectionService.Dispose();
                }

                if (_plcConnectionService != null)
                {
                    await _plcConnectionService.DisconnectAsync();
                    _plcConnectionService.Dispose();
                }
                
                // 釋放宏資源
                if (_macros != null)
                {
                    bool releaseResult = false;
                    _macros.ReleaseRobot(out releaseResult);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"關閉時發生錯誤：{ex.Message}", "錯誤", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region 視覺檢測與連續掃描

        /// <summary>
        /// 啟動連續掃描按鈕事件
        /// </summary>
        private async void button3_Click(object sender, EventArgs e)
        {
            if (!_isScanning)
            {
                _isScanning = true;
                button3.BackColor = Color.Red;
                await Task.Run(() => StartContinuousScan());
            }
        }

        /// <summary>
        /// 停止連續掃描按鈕事件
        /// </summary>
        private void button4_Click(object sender, EventArgs e)
        {
            _isScanning = false;
        }

        /// <summary>
        /// 連續掃描循環 - 用於視覺檢測
        /// </summary>
        private void StartContinuousScan()
        {
            while (_isScanning)
            {
                Avl.Image grabImage = new Avl.Image();
                _macros.Grab(grabImage);

                // 執行檢測
                _macros.Inspection(grabImage, out int moldNum, out Box outObject);
                string strMN = moldNum.ToString();

                // 在 UI 執行緒中更新 viewer2D
                InvokeIfNeeded(() =>
                {
                    _viewer2D.SetImage(grabImage);
                    _viewer2D.Primitives[0].SetBox(outObject);
                    _viewer2D.Primitives[0].Color = Color.Orange;
                    _viewer2D.Primitives[0].LineWidth = 50;
                    textBox3.Text = strMN;
                });

                Thread.Sleep(20);
            }
        }

        #endregion

        #region Robot 狀態更新 (數位孿生核心)

        /// <summary>
        /// 背景計時器回調 - 定期更新 Robot 狀態
        /// </summary>
        private void UpdateRobotStatusThread(object state)
        {
            if (!this.IsHandleCreated || _macros == null) return;

            try
            {
                this.BeginInvoke((MethodInvoker)delegate {
                    UpdateRobotStatus();
                });
            }
            catch (Exception ex)
            {
                InvokeIfNeeded(() =>
                {
                    textBox2.Text = $"Error: {ex.Message}";
                });
            }
        }

        /// <summary>
        /// 更新 Robot 狀態 - 關節角度與座標位置 (數位孿生同步)
        /// </summary>
        private void UpdateRobotStatus()
        {
            // 更新關節角度
            Conditional<Joint6> outJointAngles = new Conditional<Joint6>();
            _macros.GetAngle(_outResult, _outResponseString, _outResponseStringArray, outJointAngles);

            if (_outResult.Value)
            {
                Joint6 jointAngles = outJointAngles.Value;
                textBox4.Text = $"{jointAngles.J1}";
                textBox5.Text = $"{jointAngles.J2}";
                textBox6.Text = $"{jointAngles.J3}";
                textBox7.Text = $"{jointAngles.J4}";
                textBox8.Text = $"{jointAngles.J5}";
                textBox9.Text = $"{jointAngles.J6}";
            }
            else
            {
                textBox2.Text = "Error in GetAngles";
            }

            // 更新座標位置
            Conditional<Pose6> outPose = new Conditional<Pose6>();
            _macros.GetPose(_outResult, _outResponseString, _outResponseStringArray, outPose);

            if (_outResult.Value)
            {
                Pose6 pose = outPose.Value;
                textBox10.Text = $"{pose.X}";
                textBox11.Text = $"{pose.Y}";
                textBox12.Text = $"{pose.Z}";
                textBox13.Text = $"{pose.Rx}";
                textBox14.Text = $"{pose.Ry}";
                textBox15.Text = $"{pose.Rz}";
            }
            else
            {
                textBox2.Text = "Error in GetPose";
            }

            this.Refresh();
        }

        #endregion

        #region PLC 操作

        /// <summary>
        /// 寫入 PLC 寄存器
        /// </summary>
        private void WritePLCbtn_Click(object sender, EventArgs e)
        {
            try
            {
                int regAddress = int.Parse(WriteAdsTB.Text);
                int writeValue = int.Parse(WriteValueTB.Text);
                
                if (_plcConnectionService is PlcConnectionService plcService)
                {
                    _macros.WritePLC(plcService.SocketID, regAddress, writeValue);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"寫入 PLC 錯誤：{ex.Message}", "錯誤", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 讀取 PLC 寄存器
        /// </summary>
        private void ReadPLCbtn_Click(object sender, EventArgs e)
        {
            try
            {
                int startingAddress = int.Parse(ReadAdsTB.Text);
                List<int> outIntegerValue = new List<int>();
                
                if (_plcConnectionService is PlcConnectionService plcService)
                {
                    _macros.ReadPLC(plcService.SocketID, startingAddress, outIntegerValue);
                    ReadResultTB.Text = outIntegerValue[0].ToString();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"讀取 PLC 錯誤：{ex.Message}", "錯誤", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion

        #region 空實現處理

        /// <summary>
        /// DataGridView CellContentClick - 空實現
        /// </summary>
        private void dataGridView1_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            // 此處不需要任何操作
        }

        #endregion
    }
}
