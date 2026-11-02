# 16. Live Acquisition API mode

Since version 4.0 the application asks at start in which **mode** to work. The **Live Acquisition API** mode turns the PC (for example an Edge PC near the PLC) into an acquisition service controlled by another system: the external system requests via REST the temporary acquisition of some signals, Space Fieldbus Analyzer reads them with the same drivers as the interface and sends them in real time on a WebSocket to the address given in the request. In this mode the SFA interface is not loaded: only a light page opens, showing the requests received and allowing the operation to be checked.

## Choosing the mode

![mode_choice](Start page: choice between the complete interface and the Live Acquisition API.)

| N. | Control |
|---|---|
| 1 | **Space Fieldbus Analyzer**: the complete interface (Live, Recordings, Conversion, Database, tools), described in the previous chapters. |
| 2 | **Live Acquisition API**: only the service for external systems and the monitor page; no charts, no Live buffers, no recording. |

The mode stays active until the application is closed; to change it, close and restart. On a PC that must offer only the API, the mode is chosen without an operator by starting the EXE with `--mode api` (or `--mode sfa`), or with the `SPACE_MODE` environment variable.

## Before starting: the sources

The API **does not configure PLCs**: it uses the sources of the connection saved in SFA (IP, rack, slot, endpoint, certificates, ADS or MQTT parameters). To prepare them, start once in Space Fieldbus Analyzer mode, configure the connection in the Connection pop-up and connect (the configuration is saved). In the request, `sourceId` is the protocol (`s7`, `ads`, `opcua`, `mqtt`) or an **alias** defined in the API settings (for example `PLC01=s7`). A source is opened only when a session uses it and closed when the last session ends.

## Monitor page

![live_api_monitor](Live Acquisition API mode page with two active sessions, the details of one session and the requests received.)

| N. | Element |
|---|---|
| 1 | **Listening state**: address and port on which the API receives requests (red if the port is not available). |
| 2 | **Summary**: active sessions, requests received, sessions created since start, open sources with their state, PC memory. |
| 3 | **Active sessions**: session identifier, `requestId` of the external system, client address, source, number of signals, requested → effective period, state (`starting`, `connecting`, `streaming`), WebSocket state, last sequence, samples sent and expiry (TTL). |
| 4 | **Dettagli** (Details) and **Termina** (End): open the session details or close it (like a DELETE from the external system). |
| 5 | **Session details**: WebSocket destination, last error, discarded samples and reconnections; for each signal address, type, last value with quality and time, samples sent; event log (WebSocket connected, `stream_started`, read errors, `stream_stopped`). |
| 6 | **Requests received**: the last 300 REST requests with time, client, method, path, HTTP code, duration, outcome (`ok` or error code, for example `unauthorized` or `signal_address_invalid`) and `requestId`. |

Below the active sessions, the **Ended sessions** table lists the last 50 sessions with the reason they ended (`requested`, `expired`, `stream_target_unavailable`, `service_stopped`) and the last error.

## Test on this PC

![live_api_test](Test on this PC: the page acts as the external system and receives the samples on an internal receiver.)

| N. | Control |
|---|---|
| 1 | **Source**: protocols of the saved connection and aliases. |
| 2 | **Address** of the signal, as the external system would write it (S7 `DB200.DBD56`, ADS `MAIN.Temperature`, OPC UA `ns=2;s=Temperature`, MQTT `plant/motor#/load`). |
| 3 | **Type** of the value (BOOL, BYTE, WORD, DWORD, INT, DINT, REAL and the other S7 types). |
| 4 | Requested **period** in milliseconds. |
| 5 | **TTL**: maximum duration of the session in seconds. |
| 6 | **START** / **STOP** (and **Pulisci grafico**, clear chart). |
| 7 | **Test state**: session, state, WebSocket, samples received, sequence (with any gaps) and last value. |
| 8 | **Chart** of the samples received. |
| 9 | **Receiver log**: connection, `stream_started`, first sample, any `signal_status` and `stream_stopped`. |

The test follows the same path as an external system (REST request, acquisition, outbound WebSocket): if the test works but an external system receives no data, the problem is in the network, the firewall or the list of allowed hosts.

## Connection for external systems and settings

![live_api_settings](Addresses to give to the external system, API key and settings.)

| N. | Control |
|---|---|
| 1 | **Addresses** of the API to give to the external system (one per network card of the PC). |
| 2 | **API key**: to be sent in every request with the `X-Api-Key` header (or `Authorization: Bearer`). **Mostra** (Show) and **Copia** (Copy) make it available. |
| 3 | **Nuova chiave** (New key): generates a new key; the previous one is no longer valid. |
| 4 | **Sources** of the connection saved in SFA and the aliases defined. |
| 5 | Listening **port** (default 5180). |
| 6 | **Listening address**: `0.0.0.0` for every network card, or one address of the PC. |
| 7 | **Hosts allowed as streamTarget**: names or addresses to which SFA may open the WebSocket (one per line). A request to another host is refused with `invalid_stream_target`; this PC is always allowed. |
| 8 | **Source aliases**: for example `PLC01=s7`. |
| 9 | **Require the API key**: keep it on; without a key anyone on the network can read the PLC signals. |
| 10 | **Salva impostazioni** (Save settings): if the port or address changes, listening restarts; active sessions continue. |

The other settings are: concurrent sessions (8), signals per session (64), minimum period (10 ms), maximum TTL (24 hours), reconnection (initial delay 1 s, doubled at each attempt up to 30 s, 10 attempts, 120 s in total) and interval of the `samples` messages (100 ms). The settings are saved in `live_api.json` in the data folder; the sessions are not.

At the first start with address `0.0.0.0` Windows may ask to allow network access for the application: allow it for the network of the external system, or open the port in the firewall.

## Use by the external system

| Request | Response |
|---|---|
| `POST /api/v1/live-sessions` | **201 Created** with `sessionId`, `status` (`starting`), `createdAt`, `expiresAt`, `requestedSamplingPeriodMs` and `effectiveSamplingPeriodMs`. The 201 means the session is accepted: right after it SFA opens the WebSocket to `streamTarget.url`. |
| `GET /api/v1/live-sessions/{sessionId}` | Session state, with `streamConnected`. |
| `DELETE /api/v1/live-sessions/{sessionId}` | **204 No Content**: acquisition stopped, `stream_stopped` sent, WebSocket closed, resources freed. |
| `GET /api/v1/live-sessions` | List of the active sessions (diagnostics). |
| `GET /api/v1/health` | Service state (no key). |

Example request:

```
POST http://<Edge-PC>:5180/api/v1/live-sessions
X-Api-Key: <key>
{"requestId":"test-8f21","sourceId":"PLC01",
 "signals":[{"signalId":"pyrometer-entry-stand-1","address":"DB200.DBD56","dataType":"REAL","unit":"°C"}],
 "samplingPeriodMs":250,"ttlSeconds":1800,
 "streamTarget":{"protocol":"websocket","url":"ws://TEST-PC:8080/api/live-streams/test-8f21"}}
```

On the WebSocket the following arrive, in order: `stream_started` (with `schemaVersion` 1, effective period and signals), then `samples` messages with a progressive `sequence` and groups of samples (`signalId`, UTC ISO 8601 `timestamp` assigned by the acquisition PC, `value`, `quality` `good`/`uncertain`/`bad`), any `signal_status` when a signal can no longer be read or becomes valid again, and finally `stream_stopped` with the reason.

| Item | Behaviour |
|---|---|
| Period | If it is shorter than the minimum period it is raised; the response always gives the requested and the effective period. Samples are sent at the period of the session even when the source reads more often for another session. |
| Read errors | A signal that cannot be read produces `bad` samples without a value and a `signal_status` with code `acquisition_failed` (or `source_unavailable` if the source is disconnected): the last valid value is never repeated. |
| WebSocket lost | SFA retries the connection (increasing delay) and, when it succeeds, sends a new `stream_started` and restarts the sequence; samples of the disconnected period are not kept. If the destination stays unreachable beyond the limits, the session ends with `stream_target_unavailable`. |
| TTL | At expiry: `stream_stopped` with reason `expired`, WebSocket closed, session deleted. |
| REST errors | Always in the format `{"status":"error","error":{"code","message","retryable"}}`. Codes: `unauthorized` (401), `invalid_request`, `signal_address_invalid`, `signal_type_unsupported`, `sampling_period_unsupported`, `invalid_stream_target` (400), `source_not_found`, `session_not_found` (404), `session_limit_reached` (429), `source_unavailable` (503), `internal_error` (500). |
| Read-only | The API only reads: there are no write, force, setpoint or reset commands towards the PLC. |
| Persistence | Sessions exist only in memory and do not change the SFA configuration (trends, signals, workspace, recordings). The technical log and the event log remain. |

The mode does not replace network security: the key travels in clear text with `http`/`ws`. Use it on a controlled plant network.
