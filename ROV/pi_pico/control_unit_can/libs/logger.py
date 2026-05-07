import os
import time


class PicoLogger:
    def __init__(self, logfile='system.log', max_bytes=50 * 1024, sys_verbose=True, dbg_verbose=False):
        self.logfile = logfile
        self.max_bytes = int(max_bytes)
        self.sys_verbose = bool(sys_verbose)
        self.dbg_verbose = bool(dbg_verbose)

    def _timestamp(self):
        try:
            t = time.localtime()
            return "{:04d}-{:02d}-{:02d} {:02d}:{:02d}:{:02d}".format(t[0], t[1], t[2], t[3], t[4], t[5])
        except Exception:
            return "t:+{}ms".format(time.ticks_ms())

    def _ensure_log_size(self):
        try:
            st = os.stat(self.logfile)
            size = st[6]
        except OSError:
            return

        if size <= self.max_bytes:
            return

        keep = self.max_bytes // 2
        try:
            with open(self.logfile, 'rb') as f:
                if size <= keep:
                    return
                f.seek(size - keep)
                data = f.read()

            with open(self.logfile, 'wb') as f:
                header = "--- LOG TRUNCATED (keeping last {} bytes) ---\n".format(keep)
                f.write(header.encode('utf-8'))
                f.write(data)
        except Exception:
            return

    def print_system(self, msg, verbose=None):
        if verbose is None:
            verbose = self.sys_verbose

        line = "{} SYSTEM: {}\n".format(self._timestamp(), msg)

        if verbose:
            print(line, end='')

        try:
            with open(self.logfile, 'a') as f:
                f.write(line)
        except Exception as e:
            if self.dbg_verbose:
                print("Logger write failed:", e)

        self._ensure_log_size()

    def print_debug(self, msg, verbose=None):
        if verbose is None:
            verbose = self.dbg_verbose

        line = "{} DEBUG: {}".format(self._timestamp(), msg)

        if verbose:
            print(line)

    def set_system_verbose(self, v: bool):
        self.sys_verbose = bool(v)

    def set_debug_verbose(self, v: bool):
        self.dbg_verbose = bool(v)

    # ----------------------------
    # NEW HELPER FORMATTING FUNCS
    # ----------------------------

    def print_as_hex(self, byte_array, verbose=None):
        try:
            s = " ".join(f"{b:02X}" for b in byte_array)
        except Exception:
            s = str(byte_array)

        self.print_debug(f"Received data (hex): {s}", verbose=verbose)

    def print_as_bytes(self, byte_array, verbose=None):
        try:
            s = " ".join(str(int(b)) for b in byte_array)
        except Exception:
            s = str(byte_array)

        self.print_debug(f"Received data (bytes): {s}", verbose=verbose)

    def print_as_percent(self, values, verbose=None):
        try:
            s = " ".join(f"{v * 100:.1f}%" for v in values)
        except Exception:
            s = str(values)

        self.print_debug(f"Values (%): {s}", verbose=verbose)

    def print_raw_list(self, values, verbose=None):
        try:
            s = " ".join(str(v) for v in values)
        except Exception:
            s = str(values)

        self.print_debug(f"List: {s}", verbose=verbose)

    def print_can_msg(self, rxId, data, verbose=True):
        try:
            s = " ".join(f"{b:02X}" for b in data)
        except Exception:
            s = str(data)

        self.print_debug(f"RxId: {rxId} - Data: {s}", verbose=verbose)


logger = PicoLogger()


def print_system(msg, verbose=None):
    logger.print_system(msg, verbose=verbose)


def print_debug(msg, verbose=None):
    logger.print_debug(msg, verbose=verbose)