// [AI修改] 统一官方/本地额度语义；输入上限、严格数字和整份快照校验在两端只实现一次。
use serde_json::Value;
use std::{
    panic::{AssertUnwindSafe, catch_unwind},
    slice,
};

const ABI: u32 = 1;
const MAX_INPUT: usize = 1_048_576;
const INVALID: i32 = 1;
const MISSING: i32 = 2;
const DUPLICATE: i32 = 3;
const PERCENT: i32 = 4;
const TOO_LARGE: i32 = 5;
const ARGUMENT: i32 = 6;
const INTERNAL: i32 = 7;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct PedalWindow {
    pub duration_minutes: u32,
    pub has_reset: u32,
    pub used_percent: f64,
    pub resets_at: f64,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct PedalSnapshot {
    pub abi_version: u32,
    pub window_count: u32,
    pub observed_at: f64,
    pub first: PedalWindow,
    pub second: PedalWindow,
}

fn field<'a>(value: &'a Value, camel: &str, snake: &str, local: bool) -> Option<&'a Value> {
    value
        .get(camel)
        .or_else(|| if local { value.get(snake) } else { None })
}

fn parse(data: &[u8], observed_at: f64, mode: u32) -> Result<PedalSnapshot, i32> {
    if data.len() > MAX_INPUT {
        return Err(TOO_LARGE);
    }
    if !observed_at.is_finite() || observed_at <= 0.0 || mode > 1 {
        return Err(ARGUMENT);
    }
    let root: Value = serde_json::from_slice(data).map_err(|_| INVALID)?;
    let local = mode == 1;
    let bucket = if local {
        if let Some(id) = root.get("limit_id")
            && id.as_str() != Some("codex")
        {
            return Err(MISSING);
        }
        &root
    } else if let Some(by_id) = root.get("rateLimitsByLimitId").filter(|v| !v.is_null()) {
        if !by_id.is_object() {
            return Err(INVALID);
        }
        by_id.get("codex").ok_or(MISSING)?
    } else {
        root.get("rateLimits").ok_or(MISSING)?
    };
    if !bucket.is_object() || (bucket.get("primary").is_none() && bucket.get("secondary").is_none())
    {
        return Err(INVALID);
    }
    let mut windows = Vec::with_capacity(2);
    for raw in [bucket.get("primary"), bucket.get("secondary")]
        .into_iter()
        .flatten()
    {
        if raw.is_null() {
            continue;
        }
        let duration = field(raw, "windowDurationMins", "window_minutes", local)
            .and_then(Value::as_u64)
            .filter(|n| *n > 0 && *n <= u32::MAX as u64)
            .ok_or(INVALID)? as u32;
        let used = field(raw, "usedPercent", "used_percent", local)
            .and_then(Value::as_f64)
            .ok_or(INVALID)?;
        if !used.is_finite() || !(0.0..=100.0).contains(&used) {
            return Err(PERCENT);
        }
        if duration != 300 && duration != 10080 {
            continue;
        }
        if windows
            .iter()
            .any(|w: &PedalWindow| w.duration_minutes == duration)
        {
            return Err(DUPLICATE);
        }
        let reset = field(raw, "resetsAt", "resets_at", local)
            .and_then(Value::as_f64)
            .filter(|v| v.is_finite() && *v > 0.0 && *v < 253_402_300_800.0);
        windows.push(PedalWindow {
            duration_minutes: duration,
            has_reset: u32::from(reset.is_some()),
            used_percent: used,
            resets_at: reset.unwrap_or(0.0),
        });
    }
    windows.sort_by_key(|w| w.duration_minutes);
    Ok(PedalSnapshot {
        abi_version: ABI,
        window_count: windows.len() as u32,
        observed_at,
        first: windows.first().copied().unwrap_or_default(),
        second: windows.get(1).copied().unwrap_or_default(),
    })
}

fn valid(snapshot: &PedalSnapshot) -> bool {
    if snapshot.abi_version != ABI
        || snapshot.window_count > 2
        || !snapshot.observed_at.is_finite()
        || snapshot.observed_at <= 0.0
    {
        return false;
    }
    let windows = [snapshot.first, snapshot.second];
    let mut previous = 0;
    for window in windows.iter().take(snapshot.window_count as usize) {
        if ![300, 10080].contains(&window.duration_minutes)
            || window.duration_minutes <= previous
            || !window.used_percent.is_finite()
            || !(0.0..=100.0).contains(&window.used_percent)
            || window.has_reset > 1
            || (window.has_reset == 1 && (!window.resets_at.is_finite() || window.resets_at <= 0.0))
        {
            return false;
        }
        previous = window.duration_minutes;
    }
    true
}

#[unsafe(no_mangle)]
pub extern "C" fn pedal_abi_version() -> u32 {
    ABI
}

// [AI修改] 调用方须传入有效的只读输入和独占输出；复制前校验空指针/大小，panic不越过ABI。
/// # Safety
/// Non-null pointers must refer to readable input / writable aligned output for the stated size.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pedal_parse(
    data: *const u8,
    length: usize,
    observed_at: f64,
    mode: u32,
    output: *mut PedalSnapshot,
) -> i32 {
    if output.is_null() {
        return ARGUMENT;
    }
    unsafe {
        output.write(PedalSnapshot::default());
    }
    if length > MAX_INPUT {
        return TOO_LARGE;
    }
    if data.is_null() || length == 0 {
        return ARGUMENT;
    }
    let result = catch_unwind(AssertUnwindSafe(|| {
        parse(
            unsafe { slice::from_raw_parts(data, length) },
            observed_at,
            mode,
        )
    }));
    match result {
        Ok(Ok(snapshot)) => {
            unsafe {
                output.write(snapshot);
            }
            0
        }
        Ok(Err(code)) => code,
        Err(_) => INTERNAL,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn pedal_remaining(used: f64) -> f64 {
    if used.is_finite() && (0.0..=100.0).contains(&used) {
        100.0 - used
    } else {
        f64::NAN
    }
}

// [AI修改] 同一时钟与阈值控制所有显示出口，复位过期/失败/时钟倒退均不能伪装实时。
/// # Safety
/// A non-null snapshot must point to a readable aligned PedalSnapshot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pedal_is_stale(
    snapshot: *const PedalSnapshot,
    now: f64,
    refresh: f64,
    failed: u32,
) -> u32 {
    if snapshot.is_null() {
        return 1;
    }
    let snapshot = unsafe { *snapshot };
    if !valid(&snapshot)
        || failed != 0
        || !now.is_finite()
        || !refresh.is_finite()
        || refresh <= 0.0
        || now < snapshot.observed_at - 5.0
        || now - snapshot.observed_at >= (refresh * 3.0).max(180.0)
    {
        return 1;
    }
    u32::from(
        [snapshot.first, snapshot.second]
            .iter()
            .take(snapshot.window_count as usize)
            .any(|w| w.has_reset == 1 && w.resets_at <= now),
    )
}

/// # Safety
/// Non-null inputs must be readable, output writable and aligned; output may alias either input.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn pedal_apply(
    previous: *const PedalSnapshot,
    incoming: *const PedalSnapshot,
    read_status: i32,
    output: *mut PedalSnapshot,
) -> i32 {
    if output.is_null() {
        return ARGUMENT;
    }
    let old = if previous.is_null() {
        PedalSnapshot::default()
    } else {
        unsafe { *previous }
    };
    let new = if incoming.is_null() {
        PedalSnapshot::default()
    } else {
        unsafe { *incoming }
    };
    let status = if read_status == 0 && !valid(&new) {
        INVALID
    } else {
        read_status
    };
    unsafe {
        output.write(if status == 0 { new } else { old });
    }
    status
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    fn window(duration: u32, used: f64) -> Value {
        json!({"windowDurationMins":duration,"usedPercent":used})
    }
    fn read(value: Value) -> Result<PedalSnapshot, i32> {
        parse(value.to_string().as_bytes(), 1800000000.0, 0)
    }
    fn bucket(primary: Value, secondary: Value) -> Value {
        json!({"rateLimits":{"primary":primary,"secondary":secondary}})
    }
    #[test]
    fn remaining_93() {
        assert_eq!(pedal_remaining(7.0), 93.0);
    }
    #[test]
    fn percent_boundaries() {
        assert_eq!(pedal_remaining(0.0), 100.0);
        assert_eq!(pedal_remaining(100.0), 0.0);
    }
    #[test]
    fn invalid_remaining() {
        for x in [-1.0, 101.0, f64::NAN, f64::INFINITY] {
            assert!(pedal_remaining(x).is_nan());
        }
    }
    #[test]
    fn single_week() {
        let s = read(bucket(window(10080, 7.0), Value::Null)).unwrap();
        assert_eq!(s.window_count, 1);
        assert_eq!(s.first.duration_minutes, 10080);
    }
    #[test]
    fn dual_sorted() {
        let s = read(bucket(window(10080, 7.0), window(300, 24.0))).unwrap();
        assert_eq!(s.first.duration_minutes, 300);
        assert_eq!(s.second.duration_minutes, 10080);
    }
    #[test]
    fn five_only_valid() {
        assert_eq!(
            read(bucket(window(300, 7.0), Value::Null))
                .unwrap()
                .window_count,
            1
        );
    }
    #[test]
    fn explicit_empty_valid() {
        assert_eq!(
            read(bucket(Value::Null, Value::Null)).unwrap().window_count,
            0
        );
    }
    #[test]
    fn missing_fields_fail() {
        for v in [json!({}), json!({"rateLimits":{}}), json!({"rateLimits":3})] {
            assert!(read(v).is_err());
        }
    }
    #[test]
    fn unknown_ignored() {
        assert_eq!(
            read(bucket(window(43800, 7.0), window(10080, 7.0)))
                .unwrap()
                .window_count,
            1
        );
    }
    #[test]
    fn unknown_only_empty() {
        assert_eq!(
            read(bucket(window(60, 7.0), Value::Null))
                .unwrap()
                .window_count,
            0
        );
    }
    #[test]
    fn invalid_percent_rejected() {
        for x in [-1.0, 101.0] {
            assert_eq!(read(bucket(window(10080, x), Value::Null)), Err(PERCENT));
        }
    }
    #[test]
    fn duplicate_week() {
        assert_eq!(
            read(bucket(window(10080, 7.0), window(10080, 8.0))),
            Err(DUPLICATE)
        );
    }
    #[test]
    fn duplicate_five() {
        assert_eq!(
            read(bucket(window(300, 7.0), window(300, 8.0))),
            Err(DUPLICATE)
        );
    }
    #[test]
    fn codex_bucket_wins() {
        let v = json!({"rateLimits":{"primary":window(300,99.0)},"rateLimitsByLimitId":{"codex":{"primary":window(10080,7.0)}}});
        assert_eq!(read(v).unwrap().first.used_percent, 7.0);
    }
    #[test]
    fn missing_codex_never_falls_back() {
        let v = json!({"rateLimits":{"primary":window(10080,99.0)},"rateLimitsByLimitId":{"other":{"primary":window(10080,7.0)}}});
        assert_eq!(read(v), Err(MISSING));
    }
    #[test]
    fn malformed_map_rejected() {
        assert_eq!(read(json!({"rateLimitsByLimitId":5})), Err(INVALID));
    }
    #[test]
    fn null_map_uses_single_bucket() {
        let mut v = bucket(window(10080, 7.0), Value::Null);
        v["rateLimitsByLimitId"] = Value::Null;
        assert!(read(v).is_ok());
    }
    #[test]
    fn invalid_types_fail_whole_snapshot() {
        for value in [json!(true), json!("7"), Value::Null] {
            let mut w = window(300, 7.0);
            w["usedPercent"] = value;
            assert!(read(bucket(w, window(10080, 7.0))).is_err());
        }
    }
    #[test]
    fn invalid_duration() {
        for x in [json!(0), json!(-1), json!(300.5), json!("300")] {
            let mut w = window(300, 7.0);
            w["windowDurationMins"] = x;
            assert!(read(bucket(w, Value::Null)).is_err());
        }
    }
    #[test]
    fn optional_reset() {
        assert_eq!(
            read(bucket(window(10080, 7.0), Value::Null))
                .unwrap()
                .first
                .has_reset,
            0
        );
    }
    #[test]
    fn invalid_reset_not_invented() {
        let mut w = window(10080, 7.0);
        w["resetsAt"] = json!(-1);
        assert_eq!(read(bucket(w, Value::Null)).unwrap().first.has_reset, 0);
    }
    #[test]
    fn local_snake_case() {
        let s=parse(br#"{"primary":{"window_minutes":10080,"used_percent":7,"resets_at":1800003600},"limit_id":"codex"}"#,1800000000.0,1).unwrap();
        assert_eq!(s.first.used_percent, 7.0);
        assert_eq!(s.first.has_reset, 1);
    }
    #[test]
    fn other_local_bucket_rejected() {
        assert_eq!(
            parse(br#"{"limit_id":"other","primary":null}"#, 1800000000.0, 1),
            Err(MISSING)
        );
    }
    #[test]
    fn malformed_json() {
        assert_eq!(parse(b"{", 1800000000.0, 0), Err(INVALID));
    }
    #[test]
    fn oversized_input() {
        assert_eq!(
            parse(&vec![b' '; MAX_INPUT + 1], 1800000000.0, 0),
            Err(TOO_LARGE)
        );
    }
    #[test]
    fn bad_arguments() {
        assert_eq!(parse(b"{}", f64::NAN, 0), Err(ARGUMENT));
        assert_eq!(parse(b"{}", 1.0, 2), Err(ARGUMENT));
    }
    #[test]
    fn fresh_then_age_boundary() {
        let s = read(bucket(window(10080, 7.0), Value::Null)).unwrap();
        unsafe {
            assert_eq!(pedal_is_stale(&s, 1800000179.0, 60.0, 0), 0);
            assert_eq!(pedal_is_stale(&s, 1800000180.0, 60.0, 0), 1);
        }
    }
    #[test]
    fn reset_boundary_stale() {
        let mut s = read(bucket(window(10080, 7.0), Value::Null)).unwrap();
        s.first.has_reset = 1;
        s.first.resets_at = 1800000000.0;
        assert_eq!(unsafe { pedal_is_stale(&s, 1800000000.0, 60.0, 0) }, 1);
    }
    #[test]
    fn error_marks_stale() {
        let s = read(bucket(window(10080, 7.0), Value::Null)).unwrap();
        assert_eq!(unsafe { pedal_is_stale(&s, 1800000000.0, 60.0, 1) }, 1);
    }
    #[test]
    fn clock_reversal_stale() {
        let s = read(bucket(window(10080, 7.0), Value::Null)).unwrap();
        assert_eq!(unsafe { pedal_is_stale(&s, 1799999900.0, 60.0, 0) }, 1);
    }
    #[test]
    fn failed_apply_preserves_time_and_topology() {
        let s = read(bucket(window(300, 7.0), window(10080, 7.0))).unwrap();
        let mut out = PedalSnapshot::default();
        assert_eq!(
            unsafe { pedal_apply(&s, std::ptr::null(), INVALID, &mut out) },
            INVALID
        );
        assert_eq!(out, s);
    }
    #[test]
    fn success_replaces_topology() {
        let old = read(bucket(window(300, 7.0), window(10080, 7.0))).unwrap();
        let new = read(bucket(window(10080, 8.0), Value::Null)).unwrap();
        let mut out = old;
        assert_eq!(unsafe { pedal_apply(&old, &new, 0, &mut out) }, 0);
        assert_eq!(out, new);
    }
    #[test]
    fn invalid_new_keeps_previous() {
        let old = read(bucket(window(10080, 7.0), Value::Null)).unwrap();
        let mut out = PedalSnapshot::default();
        assert_ne!(
            unsafe { pedal_apply(&old, &PedalSnapshot::default(), 0, &mut out) },
            0
        );
        assert_eq!(out, old);
    }
    #[test]
    fn ffi_null_and_failure_clear_output() {
        let mut s = PedalSnapshot {
            window_count: 2,
            ..Default::default()
        };
        assert_eq!(
            unsafe { pedal_parse(std::ptr::null(), 1, 1.0, 0, &mut s) },
            ARGUMENT
        );
        assert_eq!(s, PedalSnapshot::default());
        assert_eq!(
            unsafe { pedal_parse(b"{}".as_ptr(), 2, 1.0, 0, std::ptr::null_mut()) },
            ARGUMENT
        );
    }
    #[test]
    fn ffi_success_matches_rust() {
        let text =
            b"{\"rateLimits\":{\"primary\":{\"usedPercent\":7,\"windowDurationMins\":10080}}}";
        let mut out = PedalSnapshot::default();
        assert_eq!(
            unsafe { pedal_parse(text.as_ptr(), text.len(), 1800000000.0, 0, &mut out) },
            0
        );
        assert_eq!(out, parse(text, 1800000000.0, 0).unwrap());
    }
    #[test]
    fn abi_layout() {
        assert_eq!(std::mem::size_of::<PedalWindow>(), 24);
        assert_eq!(std::mem::size_of::<PedalSnapshot>(), 64);
        assert_eq!(pedal_abi_version(), 1);
    }
}
