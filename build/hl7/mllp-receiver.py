#!/usr/bin/env python3
"""A deliberately independent MLLP receiver, for the interoperability script.

Written from the framing rules in the HL7 standard and the Python standard library
alone: no part of the application, and no HL7 library, is involved. That is the whole
point. A test that drives this application's encoder and then reads the result back with
the same application's decoder proves that the two agree, which they would even if both
were wrong about the standard.

It is not a substitute for testing against the hospital's actual interface engine, and it
does not claim to be one. What it establishes is narrower and still worth having: that
what goes on the wire is a frame an unrelated reader can find the edges of, that the
bytes inside it are in the character set the message says they are, and that this
application believes a receiver rather than itself.

Usage:
  mllp-receiver.py --port 2575 [--answer accept] [--dump DIR] [--delay 0]

Answers:
  accept   AA, the message was accepted.
  error    AE, with an ERR segment naming a field.
  reject   AR, the message was rejected before processing.
  silent   Read the message and answer nothing, holding the connection open.
  hangup   Read the message and close the connection without answering.
  garbage  Answer with bytes that are not a message.
  wrongid  Answer AA, but quoting a message control id that was never sent.
"""

import argparse
import os
import socket
import sys
import time

START = b"\x0b"
END = b"\x1c\x0d"


def read_frame(connection):
    """Reads one MLLP frame, returning the bytes between the wrapper, or None."""
    buffer = bytearray()

    while True:
        chunk = connection.recv(4096)

        if not chunk:
            return None

        buffer.extend(chunk)

        if buffer.find(END) >= 0:
            break

    start = buffer.find(START)

    if start < 0:
        # Reported rather than tolerated. A receiver that accepts an unframed message
        # hides exactly the defect this script exists to catch.
        sys.stderr.write("the frame did not begin with 0x0B\n")
        return bytes(buffer)

    end = buffer.find(END, start)

    return bytes(buffer[start + 1:end])


def field(segment, position):
    """One field of a segment, counting MSH-1 as the field separator itself."""
    parts = segment.split("|")

    return parts[position - 1] if 0 < position <= len(parts) else ""


def build_acknowledgement(message, answer):
    """Builds an ACK for a received message, in the message's own delimiters."""
    header = message.split("\r")[0]

    sending_application = field(header, 3)
    sending_facility = field(header, 4)
    receiving_application = field(header, 5)
    receiving_facility = field(header, 6)
    control_id = field(header, 10)
    version = field(header, 12) or "2.4"
    character_set = field(header, 18)

    if answer == "wrongid":
        control_id = "NEVERSENT"

    code = {"accept": "AA", "error": "AE", "reject": "AR", "wrongid": "AA"}[answer]

    # The roles swap on the way back, which is the part a receiver most often gets wrong
    # and therefore the part worth being right about here.
    stamp = time.strftime("%Y%m%d%H%M%S")

    msh = "|".join([
        "MSH", "^~\\&",
        receiving_application, receiving_facility,
        sending_application, sending_facility,
        stamp, "", "ACK^R01^ACK", "ACK" + stamp, "P", version,
        "", "", "", "", "", character_set,
    ])

    if code == "AA":
        return msh + "\r" + "|".join(["MSA", code, control_id])

    text = "Required field missing"

    return (
        msh + "\r"
        + "|".join(["MSA", code, control_id, text]) + "\r"
        + "|".join(["ERR", "PID^1^3^101^" + text])
    )


def main():
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--port", type=int, default=2575)
    parser.add_argument(
        "--answer",
        default="accept",
        choices=["accept", "error", "reject", "silent", "hangup", "garbage", "wrongid"])
    parser.add_argument("--dump", default="")
    parser.add_argument("--delay", type=float, default=0.0)
    parser.add_argument("--count", type=int, default=1, help="frames to read before exiting")
    options = parser.parse_args()

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", options.port))
    listener.listen(4)

    # Printed so that the script that starts this knows the port is bound, rather than
    # sleeping for a guessed interval and hoping.
    print("listening on %d, answering %s" % (options.port, options.answer), flush=True)

    received = 0

    while received < options.count:
        connection, _ = listener.accept()

        try:
            connection.settimeout(60)
            raw = read_frame(connection)

            if raw is None:
                continue

            received += 1

            if options.dump:
                os.makedirs(options.dump, exist_ok=True)
                path = os.path.join(options.dump, "frame-%03d.bin" % received)

                with open(path, "wb") as handle:
                    handle.write(raw)

                print("wrote %s, %d bytes" % (path, len(raw)), flush=True)

            if options.delay > 0:
                time.sleep(options.delay)

            if options.answer == "hangup":
                connection.close()
                continue

            if options.answer == "silent":
                # Held open deliberately. A sender must distinguish "no answer yet" from
                # "connection closed", and treat neither as success.
                time.sleep(45)
                continue

            if options.answer == "garbage":
                connection.sendall(START + b"this is not a message" + END)
                continue

            # Decoded as Latin-1 for the purpose of reading the header only: every byte
            # maps to a character, so nothing can fail here, and the fields this reads
            # are ASCII in any of the character sets in use.
            message = raw.decode("latin-1")
            acknowledgement = build_acknowledgement(message, options.answer)
            connection.sendall(START + acknowledgement.encode("latin-1") + END)
        finally:
            try:
                connection.close()
            except OSError:
                pass

    listener.close()


if __name__ == "__main__":
    main()
