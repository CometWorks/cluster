//! T-0229: periodic `malloc_trim(0)`. gateway-rs' live Rust heap stays flat (~30 MB) while VmRSS
//! steps up at every burst (fleet ramp, harness relaunch, node crash, stop) and never comes back:
//! glibc keeps the pages a burst used. `MALLOC_ARENA_MAX=2` lowered the level but a stop burst
//! still left RSS at 248 MB over 29 MB live (2026-09-27). `malloc_trim(0)` returns free pages
//! inside every arena to the OS, not only the top of the main heap.

use std::time::{Duration, Instant};

#[cfg(all(target_os = "linux", target_env = "gnu"))]
unsafe extern "C" {
    fn malloc_trim(pad: usize) -> std::os::raw::c_int;
}

/// Returns free heap memory to the OS now. True if glibc released any.
pub fn trim_now() -> bool {
    #[cfg(all(target_os = "linux", target_env = "gnu"))]
    {
        // SAFETY: malloc_trim only walks glibc's own arenas under their locks.
        unsafe { malloc_trim(0) != 0 }
    }
    #[cfg(not(all(target_os = "linux", target_env = "gnu")))]
    {
        false
    }
}

/// Trims every `GATEWAY_RS_MALLOC_TRIM_SECONDS` (default 60; 0 = off) on a thread of its own. A trim
/// holds each arena's lock while it walks it, so one that takes 50 ms or more is logged: that is
/// time a worker allocating in the same arena waited.
pub fn start() {
    let seconds = std::env::var("GATEWAY_RS_MALLOC_TRIM_SECONDS").ok().and_then(|s| s.parse().ok()).unwrap_or(60u64);
    if seconds == 0 {
        eprintln!("malloc-trim: off (GATEWAY_RS_MALLOC_TRIM_SECONDS=0)");
        return;
    }
    eprintln!("malloc-trim: every {seconds} s");
    std::thread::Builder::new()
        .name("malloc-trim".into())
        .spawn(move || loop {
            std::thread::sleep(Duration::from_secs(seconds));
            let started = Instant::now();
            let released = trim_now();
            let took = started.elapsed();
            if took >= Duration::from_millis(50) {
                eprintln!("malloc-trim: released={released} took {} ms", took.as_millis());
            }
        })
        .expect("malloc-trim thread");
}

#[cfg(all(test, target_os = "linux", target_env = "gnu"))]
mod tests {
    use super::trim_now;

    fn rss_kb() -> u64 {
        let status = std::fs::read_to_string("/proc/self/status").unwrap();
        let line = status.lines().find(|l| l.starts_with("VmRSS:")).unwrap();
        line.split_whitespace().nth(1).unwrap().parse().unwrap()
    }

    /// A burst of small allocations freed with a few survivors pinned through it: glibc cannot
    /// shrink the heap from the top, so RSS stays up after the free (the condition, asserted) and
    /// only malloc_trim(0) gives the pages back.
    #[test]
    fn trim_returns_a_fragmented_burst() {
        // On a thread of its own, so the burst lands in one arena that nothing else is using.
        std::thread::spawn(|| {
            let base = rss_kb();
            let mut chunks: Vec<Vec<u8>> = (0..200_000).map(|i| vec![i as u8; 1000]).collect();
            let peak = rss_kb();
            assert!(peak > base + 150_000, "the burst did not raise RSS: {base} -> {peak} kB");
            let mut survivors = Vec::new();
            for (i, chunk) in chunks.drain(..).enumerate() {
                if i % 1000 == 999 {
                    survivors.push(chunk);
                }
            }
            let freed = rss_kb();
            assert!(freed > base + 150_000, "RSS fell without a trim ({peak} -> {freed} kB): the test did not create the condition");
            trim_now();
            let trimmed = rss_kb();
            assert!(trimmed < base + 50_000, "trim left RSS at {trimmed} kB (base {base}, before trim {freed})");
            drop(survivors);
        })
        .join()
        .unwrap();
    }
}
