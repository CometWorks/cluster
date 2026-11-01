//! gateway-rs links libsteam_api.so (steamworks-sys bundles it). The binary finds it through an
//! `$ORIGIN` rpath, so this stages the library next to the binaries of this profile: target/<profile>/
//! for gateway-rs itself, and target/<profile>/deps/ for test executables. Build/build-gateway-rs.sh
//! then ships it beside the release binary. steamworks-sys is also a build-dependency, so its
//! build script has run (and unpacked the library) before this one.

use std::path::{Path, PathBuf};

fn main() {
    println!("cargo:rustc-link-arg=-Wl,-rpath,$ORIGIN");
    println!("cargo:rustc-link-arg=-Wl,--build-id=sha1");
    println!("cargo:rerun-if-changed=build.rs");
    let out = PathBuf::from(std::env::var("OUT_DIR").unwrap());
    // OUT_DIR is target/<profile>/build/gateway-rs-<hash>/out.
    let Some(profile) = out.ancestors().nth(3) else { return };
    let library = find(&profile.join("build")).expect("libsteam_api.so from steamworks-sys's build script");
    for dir in [profile.to_path_buf(), profile.join("deps")] {
        let target = dir.join("libsteam_api.so");
        let stale = std::fs::metadata(&target).ok().zip(std::fs::metadata(&library).ok()).is_none_or(|(t, l)| t.len() != l.len());
        if stale {
            let _ = std::fs::create_dir_all(&dir);
            let staging = dir.join("libsteam_api.so.tmp");
            if std::fs::copy(&library, &staging).and_then(|_| std::fs::rename(&staging, &target)).is_err() {
                println!("cargo:warning=could not stage libsteam_api.so into {}", dir.display());
            }
        }
    }
}

fn find(build: &Path) -> Option<PathBuf> {
    std::fs::read_dir(build)
        .ok()?
        .flatten()
        .filter(|entry| entry.file_name().to_string_lossy().starts_with("steamworks-sys-"))
        .map(|entry| entry.path().join("out/libsteam_api.so"))
        .find(|path| path.is_file())
}
