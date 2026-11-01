#!/usr/bin/env python3
"""Register the cluster plugins in a Magnetar config dir.

    Deploy/magnetar-register.py [--config DIR] [--binaries DIR | --cluster DIR]
                                [--direct-transport DIR] [--role node|wa|both] [--debug]

Writes ``Sources/sources.xml`` and the ``cluster-node`` / ``cluster-wa`` / ``Current`` profiles into
a Magnetar config dir (default: Magnetar's own, ``~/.config/Magnetar/Magnetar``). ``--binaries``
installs the compiled plugins of a release archive (its ``plugins/`` folder) as local plugins;
``--cluster`` (default: this checkout) registers the sources as dev folders. Direct Transport comes
from the Magnetar hub pin by default (T-0157 requirement 1, the same pin the clients load);
``--direct-transport DIR`` compiles that checkout instead, the dev override. See
Cli/magnetar_config.py for what is written and why.
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "Cli"))
from magnetar_config import write_config  # noqa: E402

HOME = Path.home()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--config", default=os.environ.get("MAGNETAR_CONFIG", str(HOME / ".config/Magnetar/Magnetar")))
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--binaries", default=os.environ.get("CLUSTER_PLUGIN_BINARIES"),
                        help="compiled plugins to install as local plugins (a release archive's plugins/)")
    source.add_argument("--cluster", default=None, help="cluster checkout to register as dev folders (default: this one)")
    transport_input = parser.add_mutually_exclusive_group()
    transport_input.add_argument("--direct-transport", default=os.environ.get("DIRECT_TRANSPORT_SOURCE"),
                        help="Direct Transport checkout to compile as a dev folder, the dev override "
                             "(default: the Magnetar hub's pinned direct-transport, T-0157)")
    transport_input.add_argument("--direct-transport-binaries", default=os.environ.get("DIRECT_TRANSPORT_BINARIES"),
                                help="compiled, pinned DirectTransport folder to install locally")
    parser.add_argument("--role", choices=["node", "wa", "both"], default="both")
    parser.add_argument("--debug", action="store_true", help="DebugBuild=true for the dev folders (default: optimized)")
    args = parser.parse_args(argv)
    if args.direct_transport_binaries and args.direct_transport:
        parser.error("DIRECT_TRANSPORT_BINARIES and DIRECT_TRANSPORT_SOURCE are mutually exclusive")
    transport = (None if not args.direct_transport or args.direct_transport.lower() == "hub"
                 else Path(args.direct_transport).expanduser())
    transport_binaries = Path(args.direct_transport_binaries) if args.direct_transport_binaries else None
    try:
        if args.binaries and not args.cluster:
            ids = write_config(Path(args.config), role=args.role, binaries=Path(args.binaries),
                               transport=transport, transport_binaries=transport_binaries, debug=args.debug)
        else:
            cluster = Path(args.cluster) if args.cluster else Path(__file__).resolve().parent.parent
            ids = write_config(Path(args.config), role=args.role, cluster=cluster,
                               transport=transport, transport_binaries=transport_binaries, debug=args.debug)
    except (FileNotFoundError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1
    print(f"registered {', '.join(ids)} in {Path(args.config).expanduser().resolve()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
