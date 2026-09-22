#!/usr/bin/env python3

import argparse
import asyncio
import time
from dataclasses import dataclass


HOST = "198.18.0.66"
PORT = 1199

ARTICLE_SIZE = 750 * 1024

LINE_LENGTH = 80
PATTERN = b"1234567890abcdefghijklmnopqrstuvwxyz"

# Apply TCP backpressure when asyncio's local transport write buffer
# reaches this size. This is deliberately much larger than one article,
# but small enough to prevent unbounded memory growth.
WRITE_BUFFER_LIMIT = 4 * 1024 * 1024

# After the sending window ends, allow responses already in flight to
# arrive before closing the connection.
RESPONSE_DRAIN_SECONDS = 2.0


@dataclass
class BenchmarkState:
    sent: int = 0
    responses: int = 0
    temporary_errors: int = 0
    bytes_sent: int = 0
    connection_errors: int = 0


def build_article_body() -> bytes:
    """
    Build one static NNTP article body.

    The body consists entirely of complete CRLF-terminated lines of
    approximately LINE_LENGTH bytes. No body line begins with '.', so
    NNTP dot-stuffing is not required for this benchmark payload.

    The resulting body is as close as possible to ARTICLE_SIZE while
    remaining line-aligned.
    """
    line = (
                   PATTERN * ((LINE_LENGTH // len(PATTERN)) + 1)
           )[:LINE_LENGTH] + b"\r\n"

    line_count = ARTICLE_SIZE // len(line)

    if line_count <= 0:
        raise ValueError("ARTICLE_SIZE is too small for one article line")

    return line * line_count


ARTICLE_BODY = build_article_body()


def build_article(message_id: str) -> bytes:
    """
    Build a complete NNTP article for TAKETHIS.

    Message-ID is unique per article; the remaining article content is
    static so that server-side processing is comparable between runs.
    """
    header = (
            b"From: benchmark@vectornntp.local\r\n"
            b"Subject: TAKETHIS benchmark\r\n"
            b"Message-ID: <"
            + message_id.encode("ascii")
            + b">\r\n"
              b"\r\n"
    )

    return header + ARTICLE_BODY + b"\r\n.\r\n"


async def apply_write_backpressure(
        writer: asyncio.StreamWriter,
) -> None:
    """
    Apply bounded TCP write buffering.

    This does NOT wait for an NNTP response.

    drain() only waits until asyncio/the OS can accept more outbound
    data. The protocol remains fully pipelined.
    """
    transport = writer.transport

    if transport is None:
        return

    if transport.get_write_buffer_size() >= WRITE_BUFFER_LIMIT:
        await writer.drain()


async def read_responses(
        reader: asyncio.StreamReader,
        state: BenchmarkState,
) -> None:
    """
    Continuously consume NNTP responses.

    Responses never pace the sender. They are consumed concurrently so
    that the server's response path cannot become blocked because the
    client isn't reading.
    """
    try:
        while True:
            response = await reader.readline()

            if not response:
                return

            if response.startswith((b"239 ", b"439 ")):
                state.responses += 1

            elif response.startswith(b"400 "):
                state.temporary_errors += 1

    except (
            ConnectionResetError,
            BrokenPipeError,
            asyncio.IncompleteReadError,
            ConnectionAbortedError,
    ):
        return


async def connection_worker(
        connection_id: int,
        duration: float,
        state: BenchmarkState,
) -> None:
    reader, writer = await asyncio.open_connection(HOST, PORT)

    try:
        # --------------------------------------------------------------
        # Greeting
        # --------------------------------------------------------------

        greeting = await reader.readline()

        if not greeting.startswith((b"201 ", b"200 ")):
            raise RuntimeError(
                f"connection {connection_id}: unexpected greeting: "
                f"{greeting!r}"
            )

        # --------------------------------------------------------------
        # Enter streaming mode
        # --------------------------------------------------------------

        writer.write(b"MODE STREAM\r\n")
        await writer.drain()

        response = await reader.readline()

        if not response.startswith(b"203 "):
            raise RuntimeError(
                f"connection {connection_id}: MODE STREAM failed: "
                f"{response!r}"
            )

        # --------------------------------------------------------------
        # Start independent response reader
        # --------------------------------------------------------------

        response_task = asyncio.create_task(
            read_responses(reader, state)
        )

        sequence = 0
        end_time = time.perf_counter() + duration

        try:
            while True:
                now = time.perf_counter()

                if now >= end_time:
                    break

                sequence += 1

                message_id = (
                    f"bench-{connection_id:02d}-"
                    f"{sequence:012d}"
                    "@vectornntp.local"
                )

                command = (
                        b"TAKETHIS <"
                        + message_id.encode("ascii")
                        + b">\r\n"
                )

                article = build_article(message_id)

                # ------------------------------------------------------
                # CRITICAL HOT PATH
                #
                # Send TAKETHIS + complete article without waiting for
                # the corresponding 239/439 response.
                # ------------------------------------------------------

                writer.write(command)
                writer.write(article)

                state.sent += 1
                state.bytes_sent += len(command) + len(article)

                # Apply bounded TCP backpressure only when the local
                # asyncio transport buffer becomes large.
                #
                # This is NOT protocol pacing.
                #
                # We are not waiting for 239.
                await apply_write_backpressure(writer)

                # Give the response reader and other benchmark workers
                # regular event-loop opportunities.
                if (sequence & 0xFF) == 0:
                    await asyncio.sleep(0)

            # ----------------------------------------------------------
            # Finish sending data already accepted by StreamWriter.
            #
            # This is still transport backpressure, not waiting for
            # NNTP responses.
            # ----------------------------------------------------------

            await writer.drain()

        finally:
            # ----------------------------------------------------------
            # Let responses already in flight arrive.
            #
            # This is deliberately outside the sending measurement
            # window.
            # ----------------------------------------------------------

            await asyncio.sleep(RESPONSE_DRAIN_SECONDS)

            response_task.cancel()

            try:
                await response_task
            except asyncio.CancelledError:
                pass

    except (
            ConnectionResetError,
            BrokenPipeError,
            ConnectionAbortedError,
            asyncio.IncompleteReadError,
    ):
        state.connection_errors += 1

    finally:
        writer.close()

        try:
            await writer.wait_closed()
        except (
                ConnectionResetError,
                BrokenPipeError,
                ConnectionAbortedError,
        ):
            pass


async def run(
        connections: int,
        duration: float,
) -> None:
    state = BenchmarkState()

    # Build one representative article once so we can report its actual
    # size. build_article() itself is still used per message because the
    # Message-ID must be unique.
    sample_message_id = "bench-00-000000000001@vectornntp.local"
    sample_article = build_article(sample_message_id)

    print()
    print("=" * 72)
    print("TAKETHIS BENCHMARK")
    print("=" * 72)
    print(f"Target:              {HOST}:{PORT}")
    print(f"Connections:         {connections}")
    print(f"Target duration:     {duration:.3f}s")
    print(f"Article body:        {len(ARTICLE_BODY):,} bytes")
    print(f"Article on wire:     {len(sample_article):,} bytes")
    print(f"Write buffer limit:  {WRITE_BUFFER_LIMIT:,} bytes")
    print()

    benchmark_start = time.perf_counter()

    workers = [
        asyncio.create_task(
            connection_worker(
                connection_id=i,
                duration=duration,
                state=state,
            )
        )
        for i in range(connections)
    ]

    # Gather all workers so that exceptions are never silently lost.
    await asyncio.gather(*workers)

    benchmark_end = time.perf_counter()

    elapsed = benchmark_end - benchmark_start

    articles_per_second = (
        state.sent / elapsed
        if elapsed > 0
        else 0.0
    )

    responses_per_second = (
        state.responses / elapsed
        if elapsed > 0
        else 0.0
    )

    gbps = (
        (state.bytes_sent * 8) / elapsed / 1e9
        if elapsed > 0
        else 0.0
    )

    response_count = state.responses

    response_ratio = (
        response_count / state.sent
        if state.sent
        else 0.0
    )

    print("=" * 72)
    print("RESULT")
    print("=" * 72)
    print(f"Elapsed:             {elapsed:.3f}s")
    print()
    print(f"TAKETHIS sent:       {state.sent:,}")
    print(f"239/439 received:    {state.responses:,}")
    print(f"Temporary 400s:      {state.temporary_errors:,}")
    print(f"Connection errors:   {state.connection_errors:,}")
    print()
    print(f"TAKETHIS/sec:        {articles_per_second:,.1f}")
    print(f"Responses/sec:       {responses_per_second:,.1f}")
    print(f"Wire throughput:     {gbps:,.3f} Gbit/s")
    print()
    print(f"Response ratio:      {response_ratio:.4%}")
    print("=" * 72)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Raw TCP RFC 4644 TAKETHIS pipeline benchmark"
    )

    parser.add_argument(
        "-c",
        "--connections",
        type=int,
        choices=(1, 10, 50),
        default=10,
        help="number of concurrent TCP connections",
    )

    parser.add_argument(
        "-d",
        "--duration",
        type=float,
        default=60.0,
        help="benchmark sending duration in seconds",
    )

    args = parser.parse_args()

    if args.duration <= 0:
        parser.error("duration must be greater than zero")

    asyncio.run(
        run(
            connections=args.connections,
            duration=args.duration,
        )
    )


if __name__ == "__main__":
    main()

