// RevWebFileSave.jslib —— 主档和备份提交后，把浏览器内的改动排入 IndexedDB 写回队列。
mergeInto(LibraryManager.library, {
    $RevWebFileSaveSync__deps: ['$FS'],
    $RevWebFileSaveSync: {
        // 回退路径的写回正在进行时，把额外的写请求合并成下一次。
        active: false,
        pending: false,

        // 供没有 Unity 文件系统队列的版本使用。一次写回只有一个在途事务。
        flush: function () {
            if (RevWebFileSaveSync.active) {
                RevWebFileSaveSync.pending = true;
                return;
            }
            RevWebFileSaveSync.active = true;
            RevWebFileSaveSync.pending = false;

            // 即使浏览器拒绝持久化也释放当前事务，下一次存档仍可发起写回。
            function completed(error) {
                RevWebFileSaveSync.active = false;
                if (error) {
                    console.warn('[RevSave] 浏览器存档写回失败', error.name || 'IndexedDB');
                }
                if (RevWebFileSaveSync.pending) {
                    RevWebFileSaveSync.flush();
                }
            }

            try {
                FS.syncfs(false, completed);
            } catch (error) {
                completed(error);
            }
        }
    },

    RevWebFileSave_Queue__deps: ['$RevWebFileSaveSync'],
    // Unity 2022.3 的 WebGL Player 已有文件系统队列。与 PlayerPrefs 使用同一队列可避免并发同步。
    RevWebFileSave_Queue: function () {
        if (typeof _JS_FileSystem_Sync === 'function') {
            _JS_FileSystem_Sync();
            return;
        }
        RevWebFileSaveSync.flush();
    }
});
