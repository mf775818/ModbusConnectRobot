using System;
using System.Threading;
using System.Threading.Tasks;
using ModbusConnectRobot.Interfaces;

namespace ModbusConnectRobot.Services
{
    /// <summary>
    /// 工業級 Robot 操作服務 - 封裝所有 Robot 控制邏輯
    /// </summary>
    public class RobotOperationService : IRobotOperationService
    {
        private readonly ProgramMacrofilters _macros;
        private readonly SynchronizationContext _uiContext;
        private readonly SemaphoreSlim _operationSemaphore = new SemaphoreSlim(1, 1);

        public RobotOperationService(ProgramMacrofilters macros, SynchronizationContext uiContext = null)
        {
            _macros = macros ?? throw new ArgumentNullException(nameof(macros));
            _uiContext = uiContext ?? SynchronizationContext.Current;
        }

        public async Task<bool> GetJointAnglesAsync(out Joint6 jointAngles)
        {
            jointAngles = default(Joint6);
            
            await _operationSemaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    Conditional<bool> outResult = new Conditional<bool>();
                    Conditional<string> outResponseString = new Conditional<string>();
                    Conditional<List<string>> outResponseStringArray = new Conditional<List<string>>();
                    Conditional<Joint6> outJointAngles = new Conditional<Joint6>();

                    _macros.GetAngle(outResult, outResponseString, outResponseStringArray, outJointAngles);

                    if (outResult.Value)
                    {
                        jointAngles = outJointAngles.Value;
                        return true;
                    }
                    return false;
                });
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        public async Task<bool> GetPoseAsync(out Pose6 pose)
        {
            pose = default(Pose6);
            
            await _operationSemaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    Conditional<bool> outResult = new Conditional<bool>();
                    Conditional<string> outResponseString = new Conditional<string>();
                    Conditional<List<string>> outResponseStringArray = new Conditional<List<string>>();
                    Conditional<Pose6> outPose = new Conditional<Pose6>();

                    _macros.GetPose(outResult, outResponseString, outResponseStringArray, outPose);

                    if (outResult.Value)
                    {
                        pose = outPose.Value;
                        return true;
                    }
                    return false;
                });
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        public async Task<bool> MoveJogStartAsync(JogType jogDirection)
        {
            await _operationSemaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    Conditional<bool> outResult = new Conditional<bool>();
                    Conditional<string> outResponseString = new Conditional<string>();
                    Conditional<List<string>> outResponseStringArray = new Conditional<List<string>>();

                    _macros.MoveJogStart(jogDirection, outResult, outResponseString, outResponseStringArray);
                    return outResult.Value;
                });
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        public async Task<bool> MoveJogStopAsync()
        {
            await _operationSemaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    Conditional<bool> outResult = new Conditional<bool>();
                    Conditional<string> outResponseString = new Conditional<string>();
                    Conditional<List<string>> outResponseStringArray = new Conditional<List<string>>();

                    _macros.MoveJogStop(outResult, outResponseString, outResponseStringArray);
                    return outResult.Value;
                });
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        public async Task<bool> MoveLAsync(int speed, int acceleration, Pose6 targetPose)
        {
            await _operationSemaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    Conditional<bool> outResult = new Conditional<bool>();
                    Conditional<string> outResponseString = new Conditional<string>();
                    Conditional<List<string>> outResponseStringArray = new Conditional<List<string>>();

                    _macros.MoveL(speed, acceleration, targetPose, outResult, outResponseString, outResponseStringArray);
                    return outResult.Value;
                });
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        public async Task<bool> StartDragModeAsync()
        {
            await _operationSemaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    Conditional<bool> outResult = new Conditional<bool>();
                    Conditional<string> outResponseString = new Conditional<string>();
                    Conditional<List<string>> outResponseStringArray = new Conditional<List<string>>();

                    _macros.StartDrag(outResult, outResponseString, outResponseStringArray);
                    return outResult.Value;
                });
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        public async Task<bool> StopDragModeAsync()
        {
            await _operationSemaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    Conditional<bool> outResult = new Conditional<bool>();
                    Conditional<string> outResponseString = new Conditional<string>();
                    Conditional<List<string>> outResponseStringArray = new Conditional<List<string>>();

                    _macros.StopDrag(outResult, outResponseString, outResponseStringArray);
                    return outResult.Value;
                });
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        public async Task<bool> SetSpeedFactorAsync(int speedFactor)
        {
            await _operationSemaphore.WaitAsync();
            try
            {
                return await Task.Run(() =>
                {
                    Conditional<bool> outResult = new Conditional<bool>();
                    Conditional<string> outResponseString = new Conditional<string>();
                    Conditional<List<string>> outResponseStringArray = new Conditional<List<string>>();

                    bool success = _macros.SpeedFactor(speedFactor, outResult, outResponseString, outResponseStringArray);
                    return success && outResult.Value;
                });
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }

        public async Task ReleaseRobotAsync()
        {
            await _operationSemaphore.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    _macros.ReleaseRobot(out bool result);
                });
            }
            finally
            {
                _operationSemaphore.Release();
            }
        }
    }
}
