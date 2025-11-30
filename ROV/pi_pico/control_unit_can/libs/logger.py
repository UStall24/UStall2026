import os
import time


class PicoLogger:
    def __init__(self, logfile='system.log', max_bytes=50 * 1024, sys_verbose=True, dbg_verbose=False):
        self.logfile = logfile
        self.max_bytes = int(max_bytes)
        self.sys_verbose = bool(sys_verbose)
        self.dbg_verbose = bool(dbg_verbose)

    def _timestamp(self):
        # Prefer human date/time when RTC is set, otherwise fallback to ticks_ms
        try:
            t = time.localtime()
            return "{:04d}-{:02d}-{:02d} {:02d}:{:02d}:{:02d}".format(t[0], t[1], t[2], t[3], t[4], t[5])
        except Exception:
            return "t:+{}ms".format(time.ticks_ms())

    def _ensure_log_size(self):
        try:
            st = os.stat(self.logfile)
            # os.stat tuple: index 6 is file size on MicroPython
            size = st[6]
        except OSError:
            return

        if size <= self.max_bytes:
            return

        # Keep the last half of max_bytes to preserve recent logs
        keep = self.max_bytes // 2
        try:
            with open(self.logfile, 'rb') as f:
                # If file is smaller than keep (unlikely since size > max_bytes), just return
                if size <= keep:
                    return
                f.seek(size - keep)
                data = f.read()

            with open(self.logfile, 'wb') as f:
                header = "--- LOG TRUNCATED (keeping last {} bytes) ---\n".format(keep)
                f.write(header.encode('utf-8'))
                f.write(data)
        except Exception:
            # If truncation fails, ignore to avoid crashing the main program
            return

    def print_system(self, msg, verbose=None):
        """Prints a system-level message and appends it to the local logfile.

        If `verbose` is provided it overrides the logger's system verbosity flag.
        """
        if verbose is None:
            verbose = self.sys_verbose

        line = "{} SYSTEM: {}\n".format(self._timestamp(), msg)
        if verbose:
            # Print without double newlines
            print(line, end='')

        try:
            with open(self.logfile, 'a') as f:
                f.write(line)
        except Exception as e:
            # Best-effort reporting if debug enabled
            if self.dbg_verbose:
                print("Logger write failed:", e)

        # Maintain max file size after write
        self._ensure_log_size()

    def print_debug(self, msg, verbose=None):
        """Prints debug messages to stdout only when debug verbosity is enabled.

        Debug messages are not written to the system logfile by default.
        """
        if verbose is None:
            verbose = self.dbg_verbose

        line = "{} DEBUG: {}".format(self._timestamp(), msg)
        if verbose:
            print(line)

    def set_system_verbose(self, v: bool):
        self.sys_verbose = bool(v)

    def set_debug_verbose(self, v: bool):
        self.dbg_verbose = bool(v)


# Module-level convenience logger
logger = PicoLogger()


# Helper functions for simple import usage
def print_system(msg, verbose=None):
    logger.print_system(msg, verbose=verbose)


def print_debug(msg, verbose=None):
    logger.print_debug(msg, verbose=verbose)


def print_as_hex(byte_array, verbose=None):
    """Format a bytes/list into hex and print as a system message respecting verbosity."""
    try:
        # Accept list[int], bytes, bytearray
        s = " ".join(f"{b:02X}" for b in byte_array)
    except Exception:
        s = str(byte_array)
    print_debug(f"Received data (hex): {s}", verbose=verbose)


def print_can_msg(rxId, data, verbose=True):
    """Format and print a CAN message to system log."""
    s = " ".join(f"{b:02X}" for b in data)
    print_debug(f"RxId: {rxId} - Data: {s}", verbose=verbose)
