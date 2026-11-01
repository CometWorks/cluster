//! A minimal blocking `std::net::UdpSocket` driver around an [`Endpoint`], for tests and tools.
//! The gateway uses its own driver (SO_REUSEPORT workers, recvmmsg/sendmmsg).

use std::collections::VecDeque;
use std::io;
use std::net::{SocketAddr, UdpSocket};
use std::time::{Duration, Instant};

use crate::{Config, Endpoint, Event, Output, Packet};

struct SocketOutput<'a> {
    socket: &'a UdpSocket,
    events: &'a mut VecDeque<Event>,
    send_errors: &'a mut u64,
}

impl Output for SocketOutput<'_> {
    fn transmit(&mut self, to: SocketAddr, datagram: &[u8]) {
        // LiteNetLib ignores send failures too (SendRaw returns 0/-1).
        if self.socket.send_to(datagram, to).is_err() {
            *self.send_errors += 1;
        }
    }

    fn event(&mut self, event: Event) {
        self.events.push_back(event);
    }
}

/// `NetConstants.SocketBufferSize`: LiteNetLib sets 1 MiB send and receive buffers. The kernel
/// default (~208 KiB) overflows when a peer flushes a large burst in one update.
const SOCKET_BUFFER_SIZE: i32 = 1024 * 1024;

#[cfg(target_os = "linux")]
fn set_socket_buffers(socket: &UdpSocket, bytes: i32) {
    use std::os::fd::AsRawFd;
    unsafe extern "C" {
        fn setsockopt(
            fd: i32,
            level: i32,
            name: i32,
            value: *const std::ffi::c_void,
            len: u32,
        ) -> i32;
    }
    const SOL_SOCKET: i32 = 1;
    const SO_SNDBUF: i32 = 7;
    const SO_RCVBUF: i32 = 8;
    for option in [SO_RCVBUF, SO_SNDBUF] {
        // SAFETY: a valid socket descriptor and a pointer to a live i32 of the stated length.
        unsafe {
            setsockopt(
                socket.as_raw_fd(),
                SOL_SOCKET,
                option,
                (&bytes as *const i32).cast(),
                4,
            );
        }
    }
}

#[cfg(not(target_os = "linux"))]
fn set_socket_buffers(_socket: &UdpSocket, _bytes: i32) {}

/// One UDP socket plus one [`Endpoint`].
pub struct UdpEndpoint {
    socket: UdpSocket,
    endpoint: Endpoint,
    events: VecDeque<Event>,
    buf: Vec<u8>,
    /// Datagrams that could not be sent.
    pub send_errors: u64,
    /// Datagrams passed to the endpoint.
    pub received: u64,
}

impl UdpEndpoint {
    pub fn bind(addr: SocketAddr, config: Config) -> io::Result<Self> {
        let socket = UdpSocket::bind(addr)?;
        socket.set_nonblocking(true)?;
        set_socket_buffers(&socket, SOCKET_BUFFER_SIZE);
        Ok(UdpEndpoint {
            socket,
            endpoint: Endpoint::new(config, Instant::now()),
            events: VecDeque::new(),
            buf: vec![0; 64 * 1024],
            send_errors: 0,
            received: 0,
        })
    }

    pub fn local_addr(&self) -> io::Result<SocketAddr> {
        self.socket.local_addr()
    }

    pub fn endpoint(&self) -> &Endpoint {
        &self.endpoint
    }

    /// Runs `f` with the endpoint and an output bound to the socket and event queue, e.g.
    /// `udp.with(|ep, out| ep.connect(Instant::now(), addr, data, out))`.
    pub fn with<R>(&mut self, f: impl FnOnce(&mut Endpoint, &mut dyn Output) -> R) -> R {
        let mut out = SocketOutput {
            socket: &self.socket,
            events: &mut self.events,
            send_errors: &mut self.send_errors,
        };
        f(&mut self.endpoint, &mut out)
    }

    /// Receives everything that is waiting, then runs one logic tick. Returns the number of datagrams.
    pub fn pump(&mut self) -> io::Result<usize> {
        let mut n = 0;
        loop {
            match self.socket.recv_from(&mut self.buf) {
                Ok((len, from)) => {
                    n += 1;
                    self.received += 1;
                    let mut out = SocketOutput {
                        socket: &self.socket,
                        events: &mut self.events,
                        send_errors: &mut self.send_errors,
                    };
                    self.endpoint
                        .handle(Instant::now(), from, &self.buf[..len], &mut out);
                }
                Err(e) if e.kind() == io::ErrorKind::WouldBlock => break,
                // ICMP errors surfaced on the socket (e.g. ECONNREFUSED) are ignored, as in LiteNetLib.
                Err(e)
                    if matches!(
                        e.kind(),
                        io::ErrorKind::ConnectionRefused
                            | io::ErrorKind::ConnectionReset
                            | io::ErrorKind::Interrupted
                    ) => {}
                Err(e) => return Err(e),
            }
        }
        let mut out = SocketOutput {
            socket: &self.socket,
            events: &mut self.events,
            send_errors: &mut self.send_errors,
        };
        self.endpoint.poll(Instant::now(), &mut out);
        Ok(n)
    }

    /// Pumps until an event is available or `wait` has passed, sleeping 1 ms between idle passes.
    pub fn service(&mut self, wait: Duration) -> io::Result<()> {
        let deadline = Instant::now() + wait;
        loop {
            let n = self.pump()?;
            let now = Instant::now();
            if !self.events.is_empty() || now >= deadline {
                return Ok(());
            }
            if n == 0 {
                std::thread::sleep((deadline - now).min(Duration::from_millis(1)));
            }
        }
    }

    pub fn next_event(&mut self) -> Option<Event> {
        self.events.pop_front()
    }

    /// Waits up to `wait` for the next event.
    pub fn wait_event(&mut self, wait: Duration) -> io::Result<Option<Event>> {
        let deadline = Instant::now() + wait;
        loop {
            if let Some(e) = self.events.pop_front() {
                return Ok(Some(e));
            }
            let now = Instant::now();
            if now >= deadline {
                return Ok(None);
            }
            self.service(deadline - now)?;
        }
    }

    pub fn recycle(&mut self, packet: Packet) {
        self.endpoint.recycle(packet);
    }
}
