#if UNITY_WEBGL && !UNITY_EDITOR && (DEVELOPMENT_BUILD || REV_AUTOMATION) && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Scripting;

namespace Revolution
{
    /// <summary>普通浏览器开发包的页内主线程桥。宿主页显式启用后创建，负责跨场景请求和收尾。</summary>
    [Preserve]
    internal sealed class RevWebRuntimeAutomationHost : MonoBehaviour
    {
        /// <summary>一次跨帧动作允许等待的实际秒数，后台恢复后也按原截止时间判断。</summary>
        private const double OperationTimeoutSeconds = 30;

        /// <summary>本轮页内动作的标识，空值表示可以接下一次请求。</summary>
        private string _requestId;

        /// <summary>上一帧开始后仍待完成的动作，由本宿主推进、取消和释放。</summary>
        private RevRuntimeOperation _operation;

        /// <summary>调用动作前已记录的 Unity 错误数，用于判断本次是否出现新错误。</summary>
        private int _initialErrors;

        /// <summary>当前动作的实时时钟截止点，单位秒。</summary>
        private double _deadline;

        /// <summary>当前宿主是否拥有画布上的页内接口，销毁时只拆自己安装的接口。</summary>
        private bool _attached;

        /// <summary>检查宿主页面是否在启动前给本画布启用了开发桥。</summary>
        [DllImport("__Internal")]
        private static extern int RevWebAutomation_Requested();

        /// <summary>把页内接口连到指定 Unity 对象，返回零表示当前画布无法安装。</summary>
        [DllImport("__Internal")]
        private static extern int RevWebAutomation_Attach(string objectName);

        /// <summary>把序列化的请求回执交还浏览器；浏览器按请求标识结束等待。</summary>
        [DllImport("__Internal")]
        private static extern void RevWebAutomation_Reply(string response);

        /// <summary>拆除页内接口和帧采样钩子，结束还在等待的浏览器请求。</summary>
        [DllImport("__Internal")]
        private static extern void RevWebAutomation_Detach();

        /// <summary>第一场景加载后检查页面授权，再发布接口。游戏跨场景时继续使用同一宿主。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (RevWebAutomation_Requested() == 0)
            {
                return;
            }

            var hostObject = new GameObject("Revolution.WebAutomation");
            DontDestroyOnLoad(hostObject);
            var host = hostObject.AddComponent<RevWebRuntimeAutomationHost>();
            host._attached = RevWebAutomation_Attach(hostObject.name) != 0;
            if (!host._attached)
            {
                Destroy(hostObject);
            }
        }

        /// <summary>接收本画布发来的一次请求，入口只调用框架已注册的动作或完整 GM 名称。</summary>
        [Preserve]
        public void Receive(string json)
        {
            if (!_attached || _requestId != null)
            {
                return;
            }

            Dictionary<string, object> request;
            try
            {
                request = RevAutomationJson.Object(RevAutomationJson.Parse(json));
            }
            catch (Exception)
            {
                // 手动调用 SendMessage 时可能没有有效标识，缺少回执目标的坏报文直接丢弃。
                return;
            }
            if (!request.TryGetValue("id", out object key) || !(key is string id))
            {
                return;
            }

            _requestId = id;
            _initialErrors = RevRuntimeAutomation.ErrorCount;
            _deadline = Time.realtimeSinceStartupAsDouble + OperationTimeoutSeconds;
            try
            {
                // 和其他传输共用调用门面。动作权限、参数和 GM 确认仍由原注册表判定。
                var action = (string)request["action"];
                var arguments = RevAutomationJson.Object(request["args"]);
                bool confirm = request.TryGetValue("confirm", out object value) && value is bool enabled && enabled;
                object result;
                if (action == "gm.exec")
                {
                    if (arguments.Count != 1 || !arguments.TryGetValue("command", out object command) || !(command is string text))
                    {
                        throw new ArgumentException("gm.exec 只接收 command 字符串");
                    }

                    result = RevRuntimeAutomation.ExecuteGM(text, confirm);
                }
                else
                {
                    result = RevRuntimeAutomation.Invoke(action, RevAutomationJson.Write(arguments), confirm);
                }

                if (result is RevRuntimeOperation operation)
                {
                    _operation = operation;
                }
                else
                {
                    Complete(true, null, "完成", result);
                }
            }
            catch (Exception error)
            {
                Complete(false, error.GetType().Name, error.Message, null);
            }
        }

        /// <summary>取消对应请求，已发生的业务副作用由自己的流程收尾。</summary>
        [Preserve]
        public void Cancel(string id)
        {
            if (_requestId == id)
            {
                CancelCurrent("Cancelled", "已取消页内操作");
            }
        }

        /// <summary>每个 Unity 更新最多推进一次跨帧动作，并检查实际截止时间。</summary>
        private void Update()
        {
            if (_operation == null)
            {
                return;
            }

            if (Time.realtimeSinceStartupAsDouble >= _deadline)
            {
                CancelCurrent("Timeout", "等待页内动作完成超时");
                return;
            }

            try
            {
                if (_operation.Tick())
                {
                    Complete(_operation.Success, _operation.Success ? null : "OperationFailed", _operation.Message, _operation.Result);
                }
            }
            catch (Exception error)
            {
                Complete(false, error.GetType().Name, error.Message, null);
            }
        }

        /// <summary>结束当前等待，取消回调失败时仍继续释放该动作并返回结果。</summary>
        private void CancelCurrent(string reason, string message)
        {
            try
            {
                _operation?.Cancel();
            }
            catch (Exception error)
            {
                reason = "CancelFailed";
                message = error.Message;
            }

            Complete(false, reason, message, null);
        }

        /// <summary>归还本轮动作后发回回执。新增 Unity 错误、清理失败或序列化失败都可观察。</summary>
        private void Complete(bool success, string error, string message, object data)
        {
            string id = _requestId;
            if (id == null)
            {
                return;
            }

            // 先摘掉本轮状态，回执引发的下一次浏览器操作只会看到已经收尾的宿主。
            RevRuntimeOperation operation = _operation;
            _operation = null;
            _requestId = null;
            try
            {
                operation?.Dispose();
            }
            catch (Exception exception)
            {
                success = false;
                error = "DisposeFailed";
                message = exception.Message;
            }

            if (success && RevRuntimeAutomation.ErrorCount > _initialErrors)
            {
                success = false;
                error = "UnityError";
                message = "操作期间出现 Unity 错误，请查询 runtime.logs";
            }

            var response = new Dictionary<string, object>
            {
                ["id"] = id,
                ["success"] = success,
                ["error"] = error,
                ["message"] = message,
                ["data"] = data
            };

            string serialized;
            try
            {
                serialized = RevAutomationJson.Write(response);
            }
            catch (Exception exception)
            {
                response["success"] = false;
                response["error"] = "SerializationFailed";
                response["message"] = exception.Message;
                response["data"] = null;
                serialized = RevAutomationJson.Write(response);
            }

            RevWebAutomation_Reply(serialized);
        }

        /// <summary>播放器销毁时先结束当前业务，再拆掉页面接口和诊断状态。</summary>
        private void OnDestroy()
        {
            if (!_attached)
            {
                return;
            }

            _attached = false;
            try
            {
                CancelCurrent("Stopped", "WebGL 页内桥已关闭");
            }
            finally
            {
                RevWebAutomation_Detach();
            }
        }
    }
}
#endif
