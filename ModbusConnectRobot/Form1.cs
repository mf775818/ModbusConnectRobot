using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Threading;
using System.Text.Json;
using System.IO;

using AuroraVision;
using Avl;
using HMI.Controls;
using Atl;
using Amr;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;


namespace ModbusConnectRobot
{
    public partial class Form1 : Form
    {
        string avsProjectPath = @"C:\TK_CC\AuroraVision影像系統\TK_Sandbox\ModbusConnectAll_MarcoFilter\Program.avproj";
        public ProgramMacrofilters macros;
        private string inIP;
        private string inPLCIP;
        private System.Threading.Timer backgroundTimer;
        //private Thread thrGrab;
        //private Thread thrInspection;
        //private bool stopper = false;
        private bool isScanning = false;

        private SynchronizationContext uiContext;
        private View2DBox viewer2D = new View2DBox();

        // 初始化 Conditional 變數
        Conditional<bool> outResult = new Conditional<bool>();
        Conditional<string> outResponseString = new Conditional<string>();
        Conditional<List<string>> outResponseStringArray = new Conditional<List<string>>();
        int outSocketID;

        private bool isDraggingActive = false;

        private List<Pose6> taughtPoints = new List<Pose6>();  // 記錄點位清單
        private int pointIndex = 1;  // 用來自動編號的變數
        string pointName = "";

        private System.Drawing.Image dragActiveImage;    // 啟動狀態的圖片
        private System.Drawing.Image dragInactiveImage;  // 停止狀態的圖片
        private int currentSpeedFactor = 10;
        private bool isJogging = false;

        private bool isAutoRunning = false;
        private CancellationTokenSource autoRunCts;

        private bool isRobotConnected = false;

        public enum EquipmentStatus : int
        {
            Eq01_NotInitialed = 0,  //尚未初始化
            Eq02_Initializing = 1,  //初始化中
            Eq03_Idle = 2,          //待命中
            Eq04_Preparing = 3,     //準備中
            Eq05_AutoRunning = 4,   //自動運行中
            Eq06_CloseSystem = 5,   //系統關閉
        };
        private EquipmentStatus eqStatus = EquipmentStatus.Eq01_NotInitialed;

        // PLC


        public Form1()
        {
            InitializeComponent();
            macros = new ProgramMacrofilters(avsProjectPath);
            viewer2D.Parent = panel1;
            viewer2D.Dock = DockStyle.Fill;
            viewer2D.SizeMode = VideoBoxBase.ZoomingVideoBoxMode.Fit;
            viewer2D.InitialSizeMode = ZoomingVideoBoxSizeMode.FitToWindow;
            viewer2D.Primitives.Add();
            uiContext = SynchronizationContext.Current;

            dragActiveImage = System.Drawing.Image.FromFile(@"C:\TK_CC\AuroraVision影像系統\TK_Sandbox\CSharpCC\ModbusConnectRobot\DragOn.jpg");
            dragInactiveImage = System.Drawing.Image.FromFile(@"C:\TK_CC\AuroraVision影像系統\TK_Sandbox\CSharpCC\ModbusConnectRobot\DragOff.jpg");

            // 調整圖片大小到按鈕的大小
            dragActiveImage = ResizeImage(dragActiveImage, DragRobotbtn.Width, DragRobotbtn.Height);
            dragInactiveImage = ResizeImage(dragInactiveImage, DragRobotbtn.Width, DragRobotbtn.Height);

            DragRobotbtn.Image = dragInactiveImage;
            DragRobotbtn.ImageAlign = ContentAlignment.MiddleCenter;

        }

        private void Form1_Load(object sender, EventArgs e)
        {
            ApplyStyle();
            SetupDataGridView();

            SetControlEnabled(false); // 預設關閉所有功能
        }
        private void ChangeEquipmentStatus(EquipmentStatus status)
        {
            eqStatus = status;
        }
        private async void Connectbtn_Click(object sender, EventArgs e)
        {
            // 提前判斷 eqStatus，並加上 UI 恢復
            if (eqStatus >= EquipmentStatus.Eq02_Initializing)
            {
                MessageBox.Show("目前系統正在初始化或已連線，請稍後再試。", "無法連線", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            inIP = textBox1.Text;
            inPLCIP = textBox17.Text;
            int timeoutMilliseconds = 5000;

            // UI 提示
            textBox2.Text = "連線中，請稍候...";
            Connectbtn.Enabled = false;

            try
            {
                bool robotConnected = false;
                bool plcConnected = false;
                // 20250626_show for URVISION
                var connectTask = Task.Run(() =>
                {
                    try
                    {
                        macros.ConnectToRobot(inIP, out robotConnected);
                    }
                    catch
                    {
                        robotConnected = false;
                    }

                    try
                    {
                        macros.ConnectToPLC(inPLCIP, out outSocketID, out plcConnected);
                        //plcConnected = outPLCResult;
                    }
                    catch
                    {
                        plcConnected = false;
                    }

                    return robotConnected && plcConnected;
                });

                if (await Task.WhenAny(connectTask, Task.Delay(timeoutMilliseconds)) == connectTask)
                {
                    bool result = connectTask.Result;
                    // 20250626_show for URVISION
                    result = true;


                    if (result)
                    {
                        textBox2.Text = "Robot 與 PLC 已成功連線。";
                        Connectbtn.BackColor = Color.FromArgb(115, 191, 57);
                        ChangeEquipmentStatus(EquipmentStatus.Eq03_Idle);
                        SetControlEnabled(true);
                        SetJogSpeed(10);
                        backgroundTimer = new System.Threading.Timer(UpdateRobotStatusThread, null, 0, 500);
                        isRobotConnected = true; // ✅ 成功連線後設定
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
                        isRobotConnected = false; 
                        macros.ResetConnectRobot();
                    }
                }
                else
                {
                    MessageBox.Show("連線逾時Test，請確認設備是否正常。", "連線逾時", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    textBox2.Text = "連線逾時...";
                    SetControlEnabled(false);
                    isRobotConnected = false;
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"連線時發生錯誤：{ex.Message}", "連線錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                textBox2.Text = "連線錯誤：" + ex.Message;
                SetControlEnabled(false);
            }
            finally
            {
                Connectbtn.Enabled = true; // 保證無論如何都可再次點擊
            }


        }



        private void SetControlEnabled(bool enabled)
        {
            // 你要控制的功能按鈕
            var controlButtons = new System.Windows.Forms.Button[]
            {
                CloseSysbtn,
                Teachbtn, Playbtn, DeletePointbtn, ReindexPointbtn,
                InsertPointbtn, MoveToPointbtn, SavePointbtn, LoadPointbtn,
                WritePLCbtn, ReadPLCbtn, SetupVelbtn,
                XPosDirbtn, XNegDirbtn, YPosDirbtn, YNegDirbtn,
                ZPosDirbtn, ZNegDirbtn, DragRobotbtn
            };

            foreach (var btn in controlButtons)
            {
                btn.Enabled = enabled;            }

        }
        private void CloseSysbtn_Click(object sender, EventArgs e)
        {
            ChangeEquipmentStatus(EquipmentStatus.Eq01_NotInitialed);
            macros.ReleaseRobot(out bool result);
            if (backgroundTimer != null)
            {
                backgroundTimer.Dispose();
                backgroundTimer = null;
            }
            textBox2.Text = eqStatus.ToString();
            isRobotConnected = false;
            this.Close();
        }

        private async void button3_Click(object sender, EventArgs e)
        {
            if (!isScanning)
            {
                isScanning = true;
                button3.BackColor = Color.Red;
                await Task.Run(() => StartContinuousScan());
            }
        }
        private void StartContinuousScan()
        {
            while (isScanning)
            {
                Avl.Image Grabimage = new Avl.Image();
                macros.Grab(Grabimage);

                // **在 UI 執行緒中更新 viewer2D**
                macros.Inspection(Grabimage, out int MoldNum, out Box outObject);
                string strMN = MoldNum.ToString();

                Invoke(new Action(() =>
                {
                    viewer2D.SetImage(Grabimage);
                    viewer2D.Primitives[0].SetBox(outObject);
                    viewer2D.Primitives[0].Color = System.Drawing.Color.Orange;
                    viewer2D.Primitives[0].LineWidth = 50;
                    textBox3.Text = strMN;
                }));

                // **在 UI 執行緒中更新 textBox1**
                Task.Delay(20).Wait();
            }
        }

        private void button4_Click(object sender, EventArgs e)
        {
            isScanning = false;
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (isRobotConnected && macros != null)
            {
                try
                {
                    bool result = false;
                    macros.ReleaseRobot(out result);

                    if (result)
                    {
                        MessageBox.Show("Robot successfully released.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show("Failed to release robot. The robot might not have been connected.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"ReleaseRobot Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                // ✅ 不再誤判未連線情況
                MessageBox.Show("No connection established. Nothing to release.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }


        // 需要Chris協助地方 20250305
        //1. X + motion;
        //2. Joint space motion (單一關節移動);
        //3. 手動教點;

        private void XPosDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            if (!isJogging) // 如果尚未啟動，則啟動 Jog 運動
            {
                StartJogMovement(JogType.X_Positive);
                isJogging = true; // 將狀態設為啟動中
            }
        }
        private void XPosDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            if (isJogging) // 如果正在運行，才執行停止
            {
                StopJogMovement();
                isJogging = false; // 重置狀態
            }
        }
        private void XNegDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            if (!isJogging) // 如果尚未啟動，則啟動 Jog 運動
            {
                StartJogMovement(JogType.X_Negtive);
                isJogging = true; // 將狀態設為啟動中
            }
        }
        private void XNegDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            if (isJogging) // 如果正在運行，才執行停止
            {
                StopJogMovement();
                isJogging = false; // 重置狀態
            }
        }
        private void YPosDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            StartJogMovement(JogType.Y_Positive);
        }
        private void YPosDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            StopJogMovement();
        }
        private void YNegDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            StartJogMovement(JogType.Y_Negtive);
        }
        private void YNegDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            StopJogMovement();
        }
        private void ZPosDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            if (!isJogging) // 如果尚未啟動，則啟動 Jog 運動
            {
                StartJogMovement(JogType.Z_Positive);
                isJogging = true; // 將狀態設為啟動中
            }
        }
        private void ZPosDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            if (isJogging) // 如果正在運行，才執行停止
            {
                StopJogMovement();
                isJogging = false; // 重置狀態
            }
        }
        private void ZNegDirbtn_MouseDown(object sender, MouseEventArgs e)
        {
            if (!isJogging) // 如果尚未啟動，則啟動 Jog 運動
            {
                StartJogMovement(JogType.Z_Negtive);
                isJogging = true; // 將狀態設為啟動中
            }
        }
        private void ZNegDirbtn_MouseUp(object sender, MouseEventArgs e)
        {
            if (isJogging) // 如果正在運行，才執行停止
            {
                StopJogMovement();
                isJogging = false; // 重置狀態
            }
        }
        private void StartJogMovement(JogType jogDirection)
        {
            // 啟動 Jog 運動            
            macros.MoveJogStart(jogDirection, outResult, outResponseString, outResponseStringArray);

            // 顯示結果
            if (outResult.Value)
            {
                textBox2.Text = $"Jogging in {jogDirection} direction...";
            }
            else
            {
                textBox2.Text = "Failed to start Jog movement.";
            }
        }
        private void StopJogMovement()
        {
            // 呼叫 MoveJogStop 來停止 Jog 運動
            macros.MoveJogStop(outResult, outResponseString, outResponseStringArray);
            
            // 顯示結果
            if (outResult.Value)
            {
                textBox2.Text = "Jog movement stopped.";
            }
            else
            {
                textBox2.Text = "Failed to stop Jog movement.";
            }
        }
             
        private bool SetJogSpeed(int speedFactor)
        {
            // 設定速度
            bool successSp = macros.SpeedFactor(speedFactor, outResult, outResponseString, outResponseStringArray);

            // 檢查是否成功
            if (!outResult.Value)
            {
                textBox2.Text = "Failed to set speed factor.";
                return false;
            }

            currentSpeedFactor = speedFactor; // 更新目前速度
            textBox2.Text = $"Speed set to {speedFactor}%.";
            return true;
        }

        private void SetupVelbtn_Click(object sender, EventArgs e)
        {
            if (int.TryParse(textBox16.Text, out int newSpeed))
            {
                if (newSpeed < 0 || newSpeed > 100) // 假設速度範圍為 0-100%
                {
                    textBox2.Text = "Speed must be between 0 and 100.";
                    return;
                }
                SetJogSpeed(newSpeed);
            }
            else
            {
                textBox2.Text = "Invalid speed input.";
            }
        }
        private void UpdateRobotStatusThread(object state)
        {
            if (!this.IsHandleCreated) return;

            this.BeginInvoke((MethodInvoker)delegate {
                try
                {
                    UpdateRobotStatus(null, null);
                }
                catch (System.Exception ex)
                {
                    textBox2.Text = $"Error: {ex.Message}";
                }
            });
        }
        private void UpdateRobotStatus(object sender, EventArgs e)
        {
            Conditional<Joint6> outJointAngles = new Conditional<Joint6>();
            macros.GetAngle(outResult, outResponseString, outResponseStringArray, outJointAngles);

            // 確保 GetAngle 執行成功
            if (outResult.Value)
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

            Conditional<Pose6> outPose = new Conditional<Pose6>();
            macros.GetPose(outResult, outResponseString, outResponseStringArray, outPose);

            if (outResult.Value)
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

            this.Refresh(); // 刷新畫面
        }

        private void DragRobotbtn_Click(object sender, EventArgs e)
        {
            if (isDraggingActive)
            {
                StopDragMovement();  // 停止拖動模式
            }
            else
            {
                StartDragMovement();  // 啟動拖動模式
            }
        }
        private void StartDragMovement()
        {
            // 呼叫啟動拖動的功能
            macros.StartDrag(outResult, outResponseString, outResponseStringArray);

            if (outResult.Value)
            {
                isDraggingActive = true;  // 將狀態設為啟動
                DragRobotbtn.Image = dragActiveImage;  // 更換圖片
                textBox2.Text = "Drag Mode Activated.";
            }
            else
            {
                textBox2.Text = "Failed to start Drag mode.";
            }
        }
        private void StopDragMovement()
        {
            // 呼叫停止拖動的功能 (這假設有一個 StopDrag 函式)
            macros.StopDrag(outResult, outResponseString, outResponseStringArray);

            if (outResult.Value)
            {
                isDraggingActive = false;  // 將狀態設為停止
                DragRobotbtn.Image = dragInactiveImage;  // 更換圖片
                textBox2.Text = "Drag Mode Deactivated.";
            }
            else
            {
                textBox2.Text = "Failed to stop Drag mode.";
            }
        }
        private System.Drawing.Image ResizeImage(System.Drawing.Image image, int width, int height)
        {
            // 建立一個新的 Bitmap，並指定新的大小
            Bitmap resizedImage = new Bitmap(width, height);

            // 用 Graphics 將原圖繪製到新的 Bitmap 中
            using (Graphics graphics = Graphics.FromImage(resizedImage))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, 0, 0, width, height);
            }

            return resizedImage;
        }
        //private void ApplyStyle()
        //{
        //    Color primaryColor = Color.FromArgb(1, 104, 183);  // 深藍色 (主要按鈕)
        //    Color textColor = Color.White;

        //    string Fontformat = "Microsoft YaHei UI";
        //    // 設定視窗背景顏色
        //    //this.BackColor = backgroundColor;

        //    // 設定主要按鈕
        //    var primaryButtons = new System.Windows.Forms.Button[]
        //    {
        //        Connectbtn, CloseSysbtn, XPosDirbtn, XNegDirbtn,
        //        YPosDirbtn, YNegDirbtn, ZPosDirbtn, ZNegDirbtn,
        //        SetupVelbtn, button3, button4
        //    };

        //    foreach (var button in primaryButtons)
        //    {
        //        button.BackColor = primaryColor;
        //        button.ForeColor = textColor;
        //        button.FlatStyle = FlatStyle.Flat;
        //        button.FlatAppearance.BorderSize = 0;
        //        button.Font = new Font(Fontformat, 12, FontStyle.Bold);
        //    }
        //    // 設定文字框 (TextBox)
        //    var textBoxes = new System.Windows.Forms.TextBox[]
        //    {
        //        textBox3, textBox4, textBox5,
        //        textBox6, textBox7, textBox8, textBox9, textBox10,
        //        textBox11, textBox12, textBox13, textBox14, textBox15, textBox16
        //    };
        //    foreach (var textBox in textBoxes)
        //    {
        //        textBox.BackColor = Color.White;
        //        textBox.ForeColor = primaryColor;
        //        textBox.BorderStyle = BorderStyle.FixedSingle;                
        //        textBox.Font = new Font(Fontformat, 12, FontStyle.Bold);
        //        textBox.TextAlign = HorizontalAlignment.Center;
        //    }

        //    // 設定狀態顯示框
        //    textBox2.Font = new Font(Fontformat, 12, FontStyle.Bold);
        //    textBox2.ForeColor = primaryColor;
        //    textBox2.TextAlign = HorizontalAlignment.Center;

        //    // 設定 TabControl
        //    tabControl1.Font = new Font(Fontformat, 10, FontStyle.Bold);
        //    tabControl1.BackColor = primaryColor;
        //}
        
        private void ApplyStyle()
        {
            string fontName = "Microsoft YaHei UI";
            Font buttonFont = new Font(fontName, 12F, FontStyle.Bold);
            Font textFont = new Font(fontName, 12F, FontStyle.Bold);
            Color softBlueGray = Color.FromArgb(60, 60, 60);    // 背景柔和藍灰(200, 220, 235)
            Color buttonGrayBlue = Color.FromArgb(85, 85, 85);  // 淺藍灰按鈕(160, 180, 210)
            Color whiteText = Color.White;
            Color greenHighlight = Color.FromArgb(115, 191, 57);   // 成功提示綠

            this.BackColor = softBlueGray;

            // ---- Button 樣式統一 ----
            void StyleButton(System.Windows.Forms.Button btn)
            {
                btn.Font = buttonFont;
                btn.BackColor = buttonGrayBlue;
                btn.ForeColor = whiteText;
                btn.FlatStyle = FlatStyle.Flat;
                btn.FlatAppearance.BorderSize = 0;
            }

            // ---- TextBox 樣式統一 ----
            void StyleTextBox(System.Windows.Forms.TextBox tb)
            {
                tb.Font = textFont;
                tb.BackColor = Color.White;
                tb.ForeColor = Color.Black;
                tb.BorderStyle = BorderStyle.FixedSingle;
                tb.TextAlign = HorizontalAlignment.Center;
            }

            // 所有按鈕
            System.Windows.Forms.Button[] allButtons = {
                Connectbtn, CloseSysbtn, XPosDirbtn, XNegDirbtn, YPosDirbtn, YNegDirbtn,
                ZPosDirbtn, ZNegDirbtn, SetupVelbtn, button3, button4, Teachbtn, Playbtn,
                DeletePointbtn, ReindexPointbtn, InsertPointbtn, SavePointbtn, LoadPointbtn,
                WritePLCbtn, ReadPLCbtn, MoveToPointbtn, AutoRunbtn, DragRobotbtn
            };

            foreach (var btn in allButtons)
                StyleButton(btn);

            // 所有文字框
            System.Windows.Forms.TextBox[] allTextBoxes = {
                textBox1, textBox17, textBox2, textBox3, textBox4, textBox5,
                textBox6, textBox7, textBox8, textBox9, textBox10, textBox11,
                textBox12, textBox13, textBox14, textBox15, textBox16,
                ReadAdsTB, ReadResultTB, WriteAdsTB, WriteValueTB, MoldNumTB
            };

            foreach (var tb in allTextBoxes)
                StyleTextBox(tb);

            // 狀態顯示區
            textBox2.BackColor = Color.White;
            textBox2.ForeColor = Color.DarkSlateGray;

            // ---- DataGridView 樣式 ----
            dataGridView1.DefaultCellStyle.Font = new Font(fontName, 10, FontStyle.Regular);
            dataGridView1.ColumnHeadersDefaultCellStyle.Font = new Font(fontName, 10, FontStyle.Bold);
            dataGridView1.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            dataGridView1.ColumnHeadersDefaultCellStyle.BackColor = greenHighlight;
            dataGridView1.EnableHeadersVisualStyles = false;
            dataGridView1.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            dataGridView1.RowTemplate.Height = 28;

            // TabControl 樣式
            tabControl1.Font = new Font(fontName, 10F, FontStyle.Bold);
            tabControl1.BackColor = softBlueGray;
        }


        private void Teachbtn_Click(object sender, EventArgs e)
        {
            // 取得當前姿態
            Conditional<Pose6> currentPose = new Conditional<Pose6>();
            macros.GetPose(outResult, outResponseString, outResponseStringArray, currentPose);

            if (outResult.Value)
            {
                taughtPoints.Add(currentPose.Value);
                // 為點位命名 (例如：P1, P2, P3, ...)
                string pointName = $"P{pointIndex}";
                pointIndex++;

                // 將點位顯示在 ListBox 上
                // 將點位顯示在 DataGridView 中
                dataGridView1.Rows.Add(
                    pointName,
                    currentPose.Value.X,
                    currentPose.Value.Y,
                    currentPose.Value.Z,
                    currentPose.Value.Rx,
                    currentPose.Value.Ry,
                    currentPose.Value.Rz
                );
            }
            else
            {
                MessageBox.Show("無法取得目前位置，請再試一次。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private async void Playbtn_Click(object sender, EventArgs e)
        {
            if (taughtPoints.Count == 0)
            {
                MessageBox.Show("尚未記錄任何點位！", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            for (int i = 0; i < taughtPoints.Count; i++)
            {
                var point = taughtPoints[i];
                string pointName = dataGridView1.Rows[i].Cells[0].Value.ToString();

                bool success = await MoveToPointAsync(point);

                if (!success)
                {
                    MessageBox.Show($"點位 {pointName} 移動失敗！", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return; // 如果失敗，直接結束
                }

                // 每次移動後暫停 0.5 秒
                await Task.Delay(500);
            }
            // 將訊息顯示移動到這裡，確保所有點位執行完成後才顯示訊息
            // MessageBox.Show("所有點位已成功執行完畢！", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
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
        private async Task<bool> MoveToPointAsync(Pose6 targetPose, bool isAutoRun = false)
        {
            int inSpeed = 20;
            int inAcc = 100;

            try
            {
                macros.MoveL(inSpeed, inAcc, targetPose, outResult, outResponseString, outResponseStringArray);

                if (outResult.Value)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        textBox2.Text = isAutoRun
                            ? "AutoRun執行中"
                            : $"移動成功: X={targetPose.X}, Y={targetPose.Y}, Z={targetPose.Z}";
                    }));
                    return true;
                }
                else
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        textBox2.Text = $"移動失敗: {outResponseString.Value}";
                    }));
                    return false;
                }
            }
            catch (System.Exception ex)
            {
                this.BeginInvoke(new Action(() =>
                {
                    textBox2.Text = $"發生錯誤: {ex.Message}";
                }));
                return false;
            }
        }


        private void DeletePointbtn_Click(object sender, EventArgs e)
        {
            if (dataGridView1.SelectedRows.Count > 0)
            {
                int index = dataGridView1.SelectedRows[0].Index;

                // 移除資料
                taughtPoints.RemoveAt(index);

                // 移除 DataGridView 中的資料
                dataGridView1.Rows.RemoveAt(index);
            }
            else
            {
                MessageBox.Show("請選擇要刪除的點位。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ReindexPointbtn_Click(object sender, EventArgs e)
        {
            pointIndex = 1;  // 將點位編號重置為1

            for (int i = 0; i < dataGridView1.Rows.Count; i++)
            {
                if (dataGridView1.Rows[i].Cells[0].Value != null)
                {
                    // 重新設定點位名稱
                    string pointName = $"P{pointIndex}";
                    dataGridView1.Rows[i].Cells[0].Value = pointName;
                    pointIndex++;
                }
            }
        }

        private void InsertPointbtn_Click(object sender, EventArgs e)
        {
            if (dataGridView1.SelectedRows.Count == 0)
            {
                MessageBox.Show("請選擇要插入點位的位置。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int insertIndex = dataGridView1.SelectedRows[0].Index;

            // 取得當前姿態
            Conditional<Pose6> currentPose = new Conditional<Pose6>();
            macros.GetPose(outResult, outResponseString, outResponseStringArray, currentPose);

            if (outResult.Value)
            {
                // 將新的點位插入到清單中
                taughtPoints.Insert(insertIndex + 1, currentPose.Value);

                // 在 DataGridView 插入新點位
                dataGridView1.Rows.Insert(insertIndex + 1, "",
                    currentPose.Value.X,
                    currentPose.Value.Y,
                    currentPose.Value.Z,
                    currentPose.Value.Rx,
                    currentPose.Value.Ry,
                    currentPose.Value.Rz
                );               
            }
            else
            {
                MessageBox.Show("無法取得目前位置，請再試一次。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void MoveToPointbtn_Click(object sender, EventArgs e)
        {
            if (dataGridView1.SelectedRows.Count == 0)
            {
                MessageBox.Show("請選擇要移動的點位。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int selectedIndex = dataGridView1.SelectedRows[0].Index;

            if (selectedIndex < 0 || selectedIndex >= taughtPoints.Count)
            {
                MessageBox.Show("選擇的點位無效。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 取得選中的點位
            var targetPoint = taughtPoints[selectedIndex];
            string pointName = dataGridView1.Rows[selectedIndex].Cells[0].Value.ToString();

            bool success = await MoveToPointAsync(targetPoint);

            if (success)
            {
                MessageBox.Show($"已成功移動到點位 {pointName}。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show($"移動到點位 {pointName} 失敗！", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

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
                        // 建立匿名物件清單（Name, X, Y, Z, Rx, Ry, Rz）
                        var pointsData = taughtPoints.Select((pose, index) => new
                        {
                            Name = $"P{index + 1}",
                            X = pose.X,
                            Y = pose.Y,
                            Z = pose.Z,
                            Rx = pose.Rx,
                            Ry = pose.Ry,
                            Rz = pose.Rz
                        }).ToList();

                        // 將 JSON 序列化放入 Task 中，避免 UI 卡住
                        string json = await Task.Run(() =>
                            JsonSerializer.Serialize(pointsData, new JsonSerializerOptions { WriteIndented = true }));

                        // 寫入檔案（這是同步的，但很快）
                        System.IO.File.WriteAllText(saveFileDialog.FileName, json);

                        MessageBox.Show("點位已成功儲存！", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (System.Exception ex)
                    {
                        MessageBox.Show($"儲存點位時發生錯誤：{ex.Message}", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private async void LoadPointbtn_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "JSON Files (*.json)|*.json";
                openFileDialog.Title = "載入點位檔案";
                openFileDialog.InitialDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        string filePath = openFileDialog.FileName;

                        // 非同步讀取檔案內容
                        string json = await Task.Run(() => System.IO.File.ReadAllText(filePath));

                        // 反序列化資料
                        var pointsData = JsonSerializer.Deserialize<List<Pose6>>(json);

                        if (pointsData != null)
                        {
                            taughtPoints = pointsData;

                            string fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);

                            // 回到 UI 執行緒更新畫面
                            this.Invoke(new MethodInvoker(() =>
                            {
                                UpdateDataGridView(fileName);
                                MessageBox.Show("點位已成功載入！", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }));
                        }
                        else
                        {
                            MessageBox.Show("檔案內容格式錯誤。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        this.Invoke(new MethodInvoker(() =>
                        {
                            MessageBox.Show($"讀取檔案時發生錯誤：{ex.Message}", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }));
                    }
                }
            }
        }

        private void UpdateDataGridView(string fileName = "")
        {
            dataGridView1.Rows.Clear();
            pointIndex = 1;

            // 禁用標題欄位使用系統樣式
            dataGridView1.EnableHeadersVisualStyles = false;

            // 自訂標題樣式
            DataGridViewCellStyle headerStyle = new DataGridViewCellStyle();
            headerStyle.Font = new Font("Microsoft YaHei UI", 8, FontStyle.Bold);
            headerStyle.ForeColor = Color.White;
            headerStyle.BackColor = Color.FromArgb(115, 191, 57);
            headerStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            // 修改第一欄標題顯示檔案名稱
            if (!string.IsNullOrEmpty(fileName))
            {
                string fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(fileName);
                dataGridView1.Columns[0].HeaderText = $"PathName : {fileNameWithoutExtension}";
            }
            else
            {
                dataGridView1.Columns[0].HeaderText = "PathName";
            }

            // 套用自訂樣式
            foreach (DataGridViewColumn column in dataGridView1.Columns)
            {
                column.HeaderCell.Style = headerStyle;
            }

            foreach (var point in taughtPoints)
            {
                string pointName = $"P{pointIndex}";
                dataGridView1.Rows.Add(pointName, point.X, point.Y, point.Z, point.Rx, point.Ry, point.Rz);
                pointIndex++;
            }
        }

        private void WritePLCbtn_Click(object sender, EventArgs e)
        {
            int RegAddress = int.Parse(WriteAdsTB.Text);
            int WriteValue = int.Parse(WriteValueTB.Text);
            macros.WritePLC(outSocketID, RegAddress, WriteValue); 
        }

        private void ReadPLCbtn_Click(object sender, EventArgs e)
        {
            int startingAddress = int.Parse(ReadAdsTB.Text);  // 指定從 PLC 讀取的起始位址
            List<int> outIntegerValue = new List<int>();
            macros.ReadPLC(outSocketID, startingAddress, outIntegerValue);
            ReadResultTB.Text = outIntegerValue[0].ToString();
        }

        private void AutoRunbtn_Click(object sender, EventArgs e)
        {
            if (!isAutoRunning)
            {
                isAutoRunning = true;
                AutoRunbtn.BackColor = Color.Red;
                textBox2.Text = "AutoRun已啟動...";
                autoRunCts = new CancellationTokenSource();
                RunAutoCycleLoop(autoRunCts.Token);
            }
            else
            {
                autoRunCts?.Cancel();
                isAutoRunning = false;
                AutoRunbtn.BackColor = Color.FromArgb(1, 104, 183); // 原本的藍色
                textBox2.Text = "AutoRun已停止。";
            }
        }
        private void RunAutoCycleLoop(CancellationToken token)
        {
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int moldNum = -1;
                        Avl.Image grabImage = new Avl.Image();

                        macros.Grab(grabImage);
                        macros.Inspection(grabImage, out moldNum, out Box outObject);

                        this.BeginInvoke(new Action(() =>
                        {
                            MoldNumTB.Text = moldNum.ToString();
                            textBox2.Text = moldNum <= 0 ? "未識別到 QR code，重試中..." : $"識別成功：模具 {moldNum}";
                        }));

                        if (moldNum > 0)
                        {
                            string desktopPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory);
                            string jsonPath = System.IO.Path.Combine(desktopPath, $"MoldPath_{moldNum}.json");

                            if (!File.Exists(jsonPath))
                            {
                                this.BeginInvoke(new Action(() =>
                                {
                                    MessageBox.Show($"找不到點位檔案：{jsonPath}");
                                }));
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
                                            this.BeginInvoke(new Action(() =>
                                            {
                                                textBox2.Text = "移動失敗或中斷，自動流程中止";
                                            }));
                                            return;
                                        }
                                        await Task.Delay(500);
                                    }

                                    this.BeginInvoke(new Action(() =>
                                    {
                                        textBox2.Text = $"模具 {moldNum} 執行完畢，等待下次 QR code...";
                                    }));
                                }
                                else
                                {
                                    this.BeginInvoke(new Action(() =>
                                    {
                                        MessageBox.Show("點位資料為空。");
                                    }));
                                }
                            }
                        }

                        await Task.Delay(1000); // 每輪間隔 1 秒
                    }
                    catch (System.Exception ex)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            textBox2.Text = $"AutoRun 錯誤：{ex.Message}";
                        }));
                        await Task.Delay(1000);
                    }
                }
            }, token);
        }

        private void dataGridView1_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {

        }
    }

}
