// [AI修改] 单一版本化 C ABI：固定宽度字段、调用方内存、无跨语言分配或异常。
#ifndef CODEXPEDAL_CORE_H
#define CODEXPEDAL_CORE_H
#include <stdint.h>
#include <stddef.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct PedalWindow {
    uint32_t duration_minutes;
    uint32_t has_reset;
    double used_percent;
    double resets_at;
} PedalWindow;
typedef struct PedalSnapshot {
    uint32_t abi_version;
    uint32_t window_count;
    double observed_at;
    PedalWindow first;
    PedalWindow second;
} PedalSnapshot;
uint32_t pedal_abi_version(void);
// [AI修改] mode=0 官方 result，mode=1 本地 rate_limits 桶；失败清空输出，禁止部分成功。
int32_t pedal_parse(const uint8_t *data, size_t length, double observed_at, uint32_t mode, PedalSnapshot *output);
double pedal_remaining(double used_percent);
uint32_t pedal_is_stale(const PedalSnapshot *snapshot, double now, double refresh_seconds, uint32_t failed);
// [AI修改] 失败保留原快照和原采样时间；只接受通过校验的新成功结果。
int32_t pedal_apply(const PedalSnapshot *previous, const PedalSnapshot *incoming, int32_t read_status, PedalSnapshot *output);
#ifdef __cplusplus
}
#endif
#endif
