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

#ifdef __cplusplus
}
#endif
