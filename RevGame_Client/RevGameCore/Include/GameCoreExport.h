#pragma once
#include <stdint.h>

#if defined(_WIN32)
#if defined(RevGameCore_EXPORTS)
#define GAME_CORE_API __declspec(dllexport)
#else
#define GAME_CORE_API __declspec(dllimport)
#endif
#else
#define GAME_CORE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

// 每局一个不透明句柄。宿主线程独占；托管数组只借用到函数返回。
GAME_CORE_API void* GameCore_Create(void);
// 只接受本库创建的句柄。销毁后调用方不能再读写该实例。
GAME_CORE_API void GameCore_Destroy(void* handle);
// 全包写入；1 成功、0 背压、-1 非法。背压时调用方保留原包并稍后重试。
GAME_CORE_API int32_t GameCore_Write(void* handle, const uint8_t* data, int32_t size);
// 读取一条完整输出；0 无输出，-1 缓冲不足或内核故障。失败不会吃掉输出。
GAME_CORE_API int32_t GameCore_Read(void* handle, uint8_t* data, int32_t capacity);

// 仅在已开局、两个 FIFO 均已消费时保存帧边界。返回写入字节数，0 忙，-1 非法。
// data 为空且 capacity 为 0 时查询所需字节数，查询不推进世界。
GAME_CORE_API int32_t GameCore_SaveCheckpoint(void* handle, uint8_t* data, int32_t capacity);

// 已开局的同配置、同种子实例可恢复。1 成功并写出完整表现基线，0 忙，-1 非法。
// 恢复后的下一条输入必须是检查点帧号加一。失败时实例、FIFO 和调用方基线均保持原值。
GAME_CORE_API int32_t GameCore_RestoreCheckpoint(
    void* handle, const uint8_t* data, int32_t size, uint8_t* snapshot, int32_t capacity);

#ifdef __cplusplus
}
#endif
