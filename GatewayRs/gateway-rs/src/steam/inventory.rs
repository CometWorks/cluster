//! Skin verification runs on the Steam worker, where the GameServer inventory interface lives.
//! Never block that worker waiting for Steam or its item definitions; retain and poll the result.

use std::ffi::{CStr, c_char};
use std::task::Poll;
use std::time::{Duration, Instant};

use steamworks_sys::{self as sys, EResult};

use super::{SteamApi, SteamWorker};
use crate::ipc::{SteamSkins, types};

const TIMEOUT: Duration = Duration::from_secs(5);
const MAXIMUM_DATA: usize = 192 * 1024;

pub(super) struct SkinCheck {
    pub handle: i32,
    correlation: u32,
    session: u64,
    deadline: Instant,
}

impl<A: SteamApi> SteamWorker<A> {
    fn skin_answer(&self, correlation: u32, skins: Option<Vec<String>>) {
        self.shared.ipc.send_json(types::STEAM_SKINS, correlation, &serde_json::json!({ "skins": skins }));
    }

    pub(super) fn start_skin_check(&mut self, correlation: u32, request: SteamSkins, now: Instant) {
        use base64::Engine;
        let user = request.client_id;
        let data = (request.data.len() <= MAXIMUM_DATA * 4 / 3)
            .then(|| base64::engine::general_purpose::STANDARD.decode(&request.data))
            .and_then(Result::ok);
        if correlation == 0
            || !self.clients.get(&user).is_some_and(|client| client.session.session == request.session)
            || self.skin_checks.contains_key(&user)
            || data.as_ref().is_none_or(|data| data.is_empty() || data.len() > MAXIMUM_DATA)
        {
            self.skin_answer(correlation, None);
            return;
        }
        match self.api.inventory_start(&data.unwrap()) {
            Some(handle) => {
                self.skin_checks.insert(user, SkinCheck { handle, correlation, session: request.session, deadline: now + TIMEOUT });
            }
            None => self.skin_answer(correlation, None),
        }
    }

    pub(super) fn poll_skin_checks(&mut self, now: Instant) {
        // Take the map so answers and Steam calls do not borrow the worker through the map.
        let checks = std::mem::take(&mut self.skin_checks);
        for (user, check) in checks {
            let valid_session = self.clients.get(&user).is_some_and(|client| client.session.session == check.session);
            let answer = if !valid_session || now >= check.deadline {
                Poll::Ready(None)
            } else {
                match self.api.inventory_status(check.handle) {
                    EResult::k_EResultPending => Poll::Pending,
                    EResult::k_EResultOK if self.api.inventory_owner(check.handle, user) => {
                        self.skins_of(check.handle)
                    }
                    _ => Poll::Ready(None),
                }
            };
            if let Poll::Ready(skins) = answer {
                self.api.inventory_destroy(check.handle);
                self.skin_answer(check.correlation, skins);
            } else {
                self.skin_checks.insert(user, check);
            }
        }
    }

    /// Stay pending until all item definitions are available; never confirm a partial inventory.
    fn skins_of(&self, handle: i32) -> Poll<Option<Vec<String>>> {
        let Some(items) = self.api.inventory_items(handle) else { return Poll::Ready(None) };
        let mut skins = Vec::new();
        for item in items {
            let revoked = sys::ESteamItemFlags::k_ESteamItemRemoved.0 | sys::ESteamItemFlags::k_ESteamItemConsumed.0;
            if item.m_unQuantity == 0 || u32::from(item.m_unFlags) & revoked != 0 {
                continue; // removed or consumed items cannot grant ownership
            }
            let Some((slot, skin)) = self.api.inventory_definition(item.m_iDefinition) else { return Poll::Pending };
            if slot.is_empty() {
                return Poll::Pending;
            }
            if matches!(slot.as_str(), "Armor" | "Block") && !skin.is_empty() {
                skins.push(skin);
            }
        }
        skins.sort_unstable();
        skins.dedup();
        Poll::Ready(Some(skins))
    }

    pub(super) fn cancel_skin_check(&mut self, user: u64) {
        if let Some(check) = self.skin_checks.remove(&user) {
            self.api.inventory_destroy(check.handle);
            self.skin_answer(check.correlation, None);
        }
    }
}

// SAFETY: these helpers are only called on the Steam worker after GameServer.Init, and all
// buffers live across their synchronous FFI calls. Result handles are destroyed by the worker.
pub(super) fn api() -> *mut sys::ISteamInventory {
    unsafe { sys::SteamAPI_SteamGameServerInventory_v003() }
}

pub(super) fn start(data: &[u8]) -> Option<i32> {
    let mut handle = -1;
    let ok = unsafe { sys::SteamAPI_ISteamInventory_DeserializeResult(api(), &mut handle, data.as_ptr().cast(), data.len() as u32, false) };
    if !ok && handle != -1 {
        unsafe { sys::SteamAPI_ISteamInventory_DestroyResult(api(), handle) };
    }
    (ok && handle != -1).then_some(handle)
}

pub(super) fn items(handle: i32) -> Option<Vec<sys::SteamItemDetails_t>> {
    let mut count = 0;
    if !unsafe { sys::SteamAPI_ISteamInventory_GetResultItems(api(), handle, std::ptr::null_mut(), &mut count) } || count > 65536 {
        return None;
    }
    let mut items = vec![sys::SteamItemDetails_t { m_itemId: 0, m_iDefinition: 0, m_unQuantity: 0, m_unFlags: 0 }; count as usize];
    if count > 0 && !unsafe { sys::SteamAPI_ISteamInventory_GetResultItems(api(), handle, items.as_mut_ptr(), &mut count) } {
        return None;
    }
    items.truncate(count as usize);
    Some(items)
}

pub(super) fn property(definition: i32, name: &CStr) -> Option<String> {
    let mut buffer = [0 as c_char; 2048];
    let mut length = buffer.len() as u32;
    if !unsafe { sys::SteamAPI_ISteamInventory_GetItemDefinitionProperty(api(), definition, name.as_ptr(), buffer.as_mut_ptr(), &mut length) } {
        return None;
    }
    // Keep the read bounded even if an SDK returned an unexpected length or missing terminator.
    let bytes: Vec<u8> = buffer.iter().take(length as usize).map(|&byte| byte as u8).take_while(|&byte| byte != 0).collect();
    String::from_utf8(bytes).ok()
}
