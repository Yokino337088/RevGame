// 把托管退出请求发到这次 Unity 实例的画布，网页模板拥有 Player 的清理与关闭权。
mergeInto(LibraryManager.library, {
    // 唯一的网页 Key 控件只在 Agent 面板打开时存活，生命周期与 Unity 面板配对。
    $RevGameAgentKey: { input: null },

    // 让当前 Wasm 点击回调先返回，退出逻辑从下一次浏览器任务开始。
    RevGame_RequestBrowserQuit: function () {
        var canvas = Module.canvas;
        setTimeout(function () {
            canvas.dispatchEvent(new Event("revgame:quit"));
        }, 0);
    },

    RevGame_SetAgentKey__deps: ["$RevGameAgentKey"],
    // 使用真实密码控件，Ctrl+V、右键粘贴和退格交给浏览器。
    RevGame_SetAgentKey: function (value) {
        if (!RevGameAgentKey.input) {
            var input = document.createElement("input");
            input.type = "password";
            input.autocomplete = "off";
            input.spellcheck = false;
            input.maxLength = 512;
            input.setAttribute("aria-label", "DeepSeek API Key");
            input.id = "revgame-agent-key";
            Object.assign(input.style, {
                position: "fixed",
                display: "none",
                boxSizing: "border-box",
                border: "0",
                borderRadius: "999px",
                background: "transparent",
                color: "#142930",
                caretColor: "#142930",
                zIndex: "2",
                fontFamily: "system-ui, sans-serif"
            });
            Module.canvas.parentElement.appendChild(input);
            RevGameAgentKey.input = input;
        }
        RevGameAgentKey.input.value = value ? UTF8ToString(value) : "";
    },

    RevGame_LayoutAgentKey__deps: ["$RevGameAgentKey"],
    // 将 Unity 的归一化画布坐标换成浏览器 CSS 像素，布局在 C# 检测到尺寸变化时更新。
    RevGame_LayoutAgentKey: function (left, top, width, height) {
        var input = RevGameAgentKey.input;
        if (!input) {
            return;
        }
        var canvas = Module.canvas.getBoundingClientRect();
        var fieldHeight = height * canvas.height;
        Object.assign(input.style, {
            display: "block",
            left: (canvas.left + left * canvas.width) + "px",
            top: (canvas.top + top * canvas.height) + "px",
            width: (width * canvas.width) + "px",
            height: fieldHeight + "px",
            padding: "0 " + (fieldHeight * 0.72) + "px",
            fontSize: Math.max(14, fieldHeight * 0.36) + "px"
        });
    },

    RevGame_CopyAgentKey__deps: ["$RevGameAgentKey"],
    // 只在提交时把 UTF-8 复制进 Unity 借出的缓冲区，返回有效长度，避免跨侧释放内存。
    RevGame_CopyAgentKey: function (buffer, capacity) {
        var value = RevGameAgentKey.input ? RevGameAgentKey.input.value : "";
        var bytes = new TextEncoder().encode(value);
        var length = bytes.length;
        if (buffer && length <= capacity) {
            HEAPU8.set(bytes, buffer);
        } else {
            length = 0;
        }
        bytes.fill(0);
        return length;
    },

    RevGame_CloseAgentKey__deps: ["$RevGameAgentKey"],
    // 退场释放 DOM 和其中的输入草稿，防止覆盖下一张 Unity 面板。
    RevGame_CloseAgentKey: function () {
        if (RevGameAgentKey.input) {
            RevGameAgentKey.input.value = "";
            RevGameAgentKey.input.remove();
            RevGameAgentKey.input = null;
        }
    }
});
