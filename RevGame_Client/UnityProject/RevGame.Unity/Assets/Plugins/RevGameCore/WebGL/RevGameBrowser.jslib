// 把托管退出请求发到这次 Unity 实例的画布，网页模板拥有 Player 的清理与关闭权。
mergeInto(LibraryManager.library, {
    // 让当前 Wasm 点击回调先返回，退出逻辑从下一次浏览器任务开始。
    RevGame_RequestBrowserQuit: function () {
        var canvas = Module.canvas;
        setTimeout(function () {
            canvas.dispatchEvent(new Event("revgame:quit"));
        }, 0);
    }
});
