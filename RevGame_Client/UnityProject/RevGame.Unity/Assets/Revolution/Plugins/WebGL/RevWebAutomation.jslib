// WebGL 开发态诊断。Emscripten 为每个 Unity 实例生成独立的库状态。
mergeInto(LibraryManager.library, {
    $RevWebFrameCapture__deps: ['$Browser', '$GL'],
    $RevWebFrameCapture: {
        // 本实例只有一个采样窗口，关闭后释放缓冲和浏览器钩子。
        active: null,

        // 行内依次存申请、进入、结束、首笔绘制、绘制次数和纹理更新次数。
        stride: 6,

        // 临时替换本实例的方法。复原时保留后来接管该方法的调用方。
        hook: function (capture, target, name, make) {
            var original = target[name];
            var descriptor = Object.getOwnPropertyDescriptor(target, name);
            var wrapper = make(original);
            target[name] = wrapper;
            if (target[name] !== wrapper) {
                throw new Error('无法启用 WebGL 采样方法 ' + name);
            }

            // 只撤回自己的包装，并恢复方法原来的属性或原型查找。
            capture.restore.push(function () {
                if (target[name] === wrapper) {
                    if (descriptor) {
                        Object.defineProperty(target, name, descriptor);
                    } else {
                        delete target[name];
                    }
                }
            });
        },

        // 开始采样实际 Unity 绘制回调。最多记录 capacity 次回调，采样时只写预分配缓冲。
        start: function (canvas, capacity, countTextures) {
            if (RevWebFrameCapture.active) {
                throw new Error('已有 WebGL 帧采样，请先结束');
            }
            if (!Number.isInteger(capacity) || capacity < 2 || capacity > 120000) {
                throw new Error('帧采样容量须为 2 到 120000');
            }

            var ctx = GL.currentContext && GL.currentContext.GLctx;
            if (!ctx || ctx.canvas !== canvas || ctx.isContextLost() || Browser.mainLoop.method !== 'rAF') {
                throw new Error('请在普通 WebGL Player 已经开始用浏览器动画帧绘制后采样');
            }
            if (document.visibilityState !== 'visible') {
                throw new Error('请在可见页面中采样');
            }

            var capture = {
                begin: performance.now(), width: canvas.width, height: canvas.height,
                count: 0, capacity: capacity, overflow: false, interrupted: null, inside: false,
                firstDraw: -1, draws: 0, uploads: 0, countTextures: countTextures,
                values: new Float64Array(capacity * RevWebFrameCapture.stride), restore: []
            };
            RevWebFrameCapture.active = capture;
            try {
                // 只包装 Unity 当前画布的绘制方法，其他画布和非 Unity 回调照常运行。
                for (var name of ['drawArrays', 'drawElements', 'drawArraysInstanced', 'drawElementsInstanced', 'drawRangeElements']) {
                    if (typeof ctx[name] === 'function') {
                        RevWebFrameCapture.hook(capture, ctx, name, RevWebFrameCapture.drawHook);
                    }
                }

                if (countTextures) {
                    for (var upload of ['texImage2D', 'texSubImage2D']) {
                        RevWebFrameCapture.hook(capture, ctx, upload, RevWebFrameCapture.textureHook);
                    }
                }

                RevWebFrameCapture.hook(capture, Browser, 'requestAnimationFrame', RevWebFrameCapture.scheduleHook);

                // 一旦页面隐藏、画布变大变小或上下文丢失，结果留为诊断证据，标记为不可比较。
                function visibilityChanged() {
                    if (document.visibilityState !== 'visible') {
                        capture.interrupted = capture.interrupted || 'PageHidden';
                    }
                }

                // 保留原事件默认行为，仅标记本窗口受到了设备变化影响。
                function contextLost() {
                    capture.interrupted = capture.interrupted || 'ContextLost';
                }

                document.addEventListener('visibilitychange', visibilityChanged);
                canvas.addEventListener('webglcontextlost', contextLost);
                // 结束、取消与退出使用同一收尾清单。
                capture.restore.push(function () {
                    document.removeEventListener('visibilitychange', visibilityChanged);
                    canvas.removeEventListener('webglcontextlost', contextLost);
                });
            } catch (error) {
                RevWebFrameCapture.take();
                throw error;
            }

            return { accepted: true, width: capture.width, height: capture.height, capacity: capacity };
        },

        // 生成画布绘制包装，首笔用实际时钟计时，包括绘制之前在 Unity 内完成的工作。
        drawHook: function (original) {
            // 保留所有 WebGL 参数和原调用对象。
            return function () {
                var capture = RevWebFrameCapture.active;
                if (capture && capture.inside && capture.draws++ === 0) {
                    capture.firstDraw = performance.now();
                }
                return original.apply(this, arguments);
            };
        },

        // 详细模式额外数纹理调用次数，供定位前后对照。
        textureHook: function (original) {
            // 保持原纹理上传行为。
            return function () {
                var capture = RevWebFrameCapture.active;
                if (capture && capture.inside) {
                    capture.uploads++;
                }
                return original.apply(this, arguments);
            };
        },

        // 用 Emscripten 的实例级动画帧入口识别 Unity runner，记录本轮请求与执行时刻。
        scheduleHook: function (original) {
            // 每次请求捕获本轮窗口。旧窗口遗留的已入队回调不会写入新样本。
            return function (callback) {
                var capture = RevWebFrameCapture.active;
                if (!capture || callback !== Browser.mainLoop.runner) {
                    return original.call(this, callback);
                }

                var requested = performance.now();
                // 帧内只量时间、计数和写数值，排序与汇总留到结束。
                return original.call(this, function (timestamp) {
                    if (RevWebFrameCapture.active !== capture) {
                        return callback(timestamp);
                    }
                    if (capture.count === capture.capacity) {
                        capture.overflow = true;
                        return callback(timestamp);
                    }
                    // 模板独立调整绘制缓冲时，这轮的尺寸条件已经变化。
                    if (Module.canvas.width !== capture.width || Module.canvas.height !== capture.height) {
                        capture.interrupted = capture.interrupted || 'CanvasResized';
                    }

                    var start = performance.now();
                    capture.firstDraw = -1;
                    capture.draws = 0;
                    capture.uploads = 0;
                    capture.inside = true;
                    try {
                        return callback(timestamp);
                    } finally {
                        capture.inside = false;
                        var offset = capture.count++ * RevWebFrameCapture.stride;
                        capture.values[offset] = requested;
                        capture.values[offset + 1] = start;
                        capture.values[offset + 2] = performance.now();
                        capture.values[offset + 3] = capture.firstDraw;
                        capture.values[offset + 4] = capture.draws;
                        capture.values[offset + 5] = capture.uploads;
                    }

                });
            };
        },

        // 关闭采样并按逆序拆除自己安装的钩子。
        take: function () {
            var capture = RevWebFrameCapture.active;
            if (capture) {
                RevWebFrameCapture.active = null;
                for (var i = capture.restore.length - 1; i >= 0; i--) {
                    capture.restore[i]();
                }
            }
            return capture;
        },

        // 生成分位数和超线计数。空窗口返回 null，保留未测状态。
        distribution: function (values) {
            if (values.length === 0) {
                return null;
            }
            // 只在窗口结束后排序，采样期间不会分配这里的临时数组。
            values.sort(function (a, b) { return a - b; });
            // 最近秩分位数，全部单位为毫秒。
            function percentile(ratio) {
                return values[Math.ceil(values.length * ratio) - 1];
            }
            return {
                samples: values.length, p50: percentile(0.50), p95: percentile(0.95),
                p99: percentile(0.99), max: values[values.length - 1]
            };
        },

        // 停止并汇总绘制间隔。两次绘制之间的更新回调工时也算入工作，不塞进等待。
        stop: function () {
            var end = performance.now();
            var capture = RevWebFrameCapture.take();
            if (!capture) {
                throw new Error('当前没有 WebGL 帧采样');
            }

            var stride = RevWebFrameCapture.stride;
            var values = capture.values;
            var frameMs = [], callbackMs = [], idleMs = [], beforeDrawMs = [], slow = [];
            var lastDraw = -1, draws = 0, workSinceDraw = 0;
            var over25 = 0, over50 = 0;
            for (var i = 0; i < capture.count * stride; i += stride) {
                var start = values[i + 1];
                var work = values[i + 2] - start;
                callbackMs.push(work);
                if (i > 0) {
                    idleMs.push(Math.max(0, start - values[i - stride + 2]));
                }
                if (values[i + 4] > 0) {
                    beforeDrawMs.push(values[i + 3] - start);
                    draws++;
                    if (lastDraw >= 0) {
                        var interval = start - values[lastDraw + 1];
                        frameMs.push(interval);
                        if (interval > 25) {
                            over25++;
                            // previous 描述上一张画面。下一张实际到来前可能执行了没有画图的更新。
                            slow.push({
                                elapsedMs: interval, callbackWorkMs: workSinceDraw,
                                betweenCallbacksMs: Math.max(0, interval - workSinceDraw),
                                previousBeforeFirstDrawMs: values[lastDraw + 3] - values[lastDraw + 1],
                                previousDrawCalls: values[lastDraw + 4],
                                previousTextureUpdates: capture.countTextures ? values[lastDraw + 5] : null,
                                updateOnlyCallbacks: (i - lastDraw) / stride - 1,
                                nextQueuedBeforePreviousEnd: values[i] <= values[i - stride + 2]
                            });
                        }
                        if (interval > 50) {
                            over50++;
                        }
                    }
                    lastDraw = i;
                    workSinceDraw = 0;
                }
                workSinceDraw += work;
            }

            // 慢帧明细最多返回最慢八次，无需把整段原始录制保存到网页或磁盘。
            slow.sort(function (a, b) { return b.elapsedMs - a.elapsedMs; });
            return {
                valid: frameMs.length > 0 && !capture.overflow && !capture.interrupted,
                overflow: capture.overflow, interrupted: capture.interrupted,
                seconds: (end - capture.begin) / 1000,
                width: capture.width, height: capture.height,
                callbacks: capture.count, drawnFrames: draws, updateOnlyCallbacks: capture.count - draws,
                fps: end > capture.begin ? draws * 1000 / (end - capture.begin) : null,
                frameIntervalMs: RevWebFrameCapture.distribution(frameMs),
                callbackWorkMs: RevWebFrameCapture.distribution(callbackMs),
                betweenCallbacksMs: RevWebFrameCapture.distribution(idleMs),
                beforeFirstDrawMs: RevWebFrameCapture.distribution(beforeDrawMs),
                over25Ms: over25, over50Ms: over50, slowest: slow.slice(0, 8)
            };
        }
    },

    $RevWebAutomation__deps: ['$RevWebFrameCapture'],
    $RevWebAutomation: {
        // 当前 Unity 实例的画布、Automation 对象与唯一在途请求。
        owner: null,

        // 启动前由宿主页标记画布。每个实例只发布自己画布上的接口。
        requested: function () {
            return !!(Module.canvas && Module.canvas.getAttribute('data-rev-automation') === 'enabled');
        },

        // 使用 Unity 文档支持的 SendMessage 在本页调用对应实例，无监听器或磁盘工单。
        attach: function (objectName) {
            var canvas = Module.canvas;
            if (!RevWebAutomation.requested() || typeof Module.SendMessage !== 'function' || 'revAutomation' in canvas) {
                return 0;
            }

            var owner = { canvas: canvas, objectName: objectName, nextId: 0, pending: null, api: null };
            owner.api = Object.freeze({
                // 返回原注册表动作的 success、error、message 与 data。
                request: function (action, args, confirm) {
                    if (RevWebAutomation.owner !== owner || owner.pending) {
                        return Promise.reject(new Error('WebGL 页内桥已关闭或仍有请求未完成'));
                    }
                    var id = String(++owner.nextId);
                    // 保存回执对象再发请求。Unity 的短动作可以在 SendMessage 返回前就完成。
                    return new Promise(function (resolve, reject) {
                        var pending = { id: id, resolve: resolve, reject: reject, timer: 0 };
                        owner.pending = pending;
                        // Unity 停摆或对象被外部替换时，网页也有独立的等待上限。
                        pending.timer = setTimeout(function () {
                            if (owner.pending === pending) {
                                owner.pending = null;
                                try {
                                    Module.SendMessage(owner.objectName, 'Cancel', id);
                                } catch (error) {
                                    reject(error);
                                    return;
                                }
                                reject(new Error('等待 Unity 页内回执超时'));
                            }
                        }, 31000);
                        try {
                            var message = JSON.stringify({ id: id, action: action, args: args === undefined ? {} : args, confirm: confirm === true });
                            Module.SendMessage(owner.objectName, 'Receive', message);
                        } catch (error) {
                            clearTimeout(pending.timer);
                            owner.pending = null;
                            reject(error);
                        }
                    });
                },

                // 取消当前等待，等 Unity 按原动作契约收尾后才结束请求。
                cancel: function () {
                    if (RevWebAutomation.owner === owner && owner.pending) {
                        Module.SendMessage(owner.objectName, 'Cancel', owner.pending.id);
                        return true;
                    }
                    return false;
                },

                // 采样开始与结束由调用方配对，可以用 finally 执行取消。
                frames: Object.freeze({
                    // capacity 是回调数。只有需要追查纹理的窗口才开启详细计数。
                    start: function (capacity, countTextures) {
                        if (RevWebAutomation.owner !== owner) {
                            throw new Error('WebGL 页内桥已关闭');
                        }
                        return RevWebFrameCapture.start(canvas, capacity === undefined ? 16384 : capacity, countTextures === true);
                    },

                    // 结束窗口，返回结构化摘要与少量慢帧。
                    stop: function () {
                        if (RevWebAutomation.owner !== owner) {
                            throw new Error('WebGL 页内桥已关闭');
                        }
                        return RevWebFrameCapture.stop();
                    },

                    // 取消采样，恢复正常调度并释放记录缓冲。
                    cancel: function () {
                        return RevWebAutomation.owner === owner && !!RevWebFrameCapture.take();
                    }
                })
            });
            Object.defineProperty(canvas, 'revAutomation', { value: owner.api, configurable: true });
            RevWebAutomation.owner = owner;
            return 1;
        },

        // 只完成与当前在途标识匹配的回执。
        reply: function (response) {
            var owner = RevWebAutomation.owner;
            if (owner && owner.pending && owner.pending.id === response.id) {
                var pending = owner.pending;
                owner.pending = null;
                clearTimeout(pending.timer);
                pending.resolve(response);
            }
        },

        // 退出或销毁时拆回当前实例所有钩子，拒绝迟到的调用。
        detach: function () {
            var owner = RevWebAutomation.owner;
            if (!owner) {
                return;
            }

            RevWebAutomation.owner = null;
            RevWebFrameCapture.take();
            if (owner.canvas.revAutomation === owner.api) {
                delete owner.canvas.revAutomation;
            }
            if (owner.pending) {
                clearTimeout(owner.pending.timer);
                owner.pending.reject(new Error('WebGL 页内桥已关闭'));
                owner.pending = null;
            }
        }
    },

    RevWebAutomation_Requested__deps: ['$RevWebAutomation'],
    // 只向编入了浏览器自动化的 C# 入口报告页面授权。
    RevWebAutomation_Requested: function () {
        return RevWebAutomation.requested() ? 1 : 0;
    },

    RevWebAutomation_Attach__deps: ['$RevWebAutomation'],
    // 将运行时的对象名交给当前画布。
    RevWebAutomation_Attach: function (objectName) {
        return RevWebAutomation.attach(UTF8ToString(objectName));
    },

    RevWebAutomation_Reply__deps: ['$RevWebAutomation'],
    // Unity 序列化结果回到本页的等待方。
    RevWebAutomation_Reply: function (json) {
        RevWebAutomation.reply(JSON.parse(UTF8ToString(json)));
    },

    RevWebAutomation_Detach__deps: ['$RevWebAutomation'],
    // 运行时销毁通知归还当前实例。
    RevWebAutomation_Detach: function () {
        RevWebAutomation.detach();
    }
});
