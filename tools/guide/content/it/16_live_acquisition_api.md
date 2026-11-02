# 16. Modalità Live Acquisition API

Dalla versione 4.0, all'avvio l'applicazione chiede in quale **modalità** lavorare. La modalità **Live Acquisition API** trasforma il PC (ad esempio un PC Edge vicino al PLC) in un servizio di acquisizione comandato da un altro sistema: il sistema esterno chiede via REST l'acquisizione temporanea di alcuni segnali, Space Fieldbus Analyzer li legge con gli stessi driver dell'interfaccia e li invia in tempo reale su un WebSocket verso l'indirizzo indicato nella richiesta. In questa modalità l'interfaccia di SFA non viene caricata: si apre solo una pagina leggera che mostra le richieste ricevute e permette di verificare il funzionamento.

## Scelta della modalità

![mode_choice](Pagina iniziale: scelta tra l'interfaccia completa e la Live Acquisition API.)

| N. | Controllo |
|---|---|
| 1 | **Space Fieldbus Analyzer**: l'interfaccia completa (Live, Registrazioni, Conversione, Database, strumenti), descritta nei capitoli precedenti. |
| 2 | **Live Acquisition API**: solo il servizio per i sistemi esterni e la pagina di monitoraggio; nessun grafico, nessun buffer Live, nessuna registrazione. |

La modalità resta attiva fino alla chiusura dell'applicazione; per cambiarla si chiude e si riavvia. Su un PC che deve offrire solo l'API, la modalità si sceglie senza operatore avviando l'EXE con `--mode api` (oppure `--mode sfa`), o con la variabile d'ambiente `SPACE_MODE`.

## Prima di iniziare: le sorgenti

L'API **non configura i PLC**: usa le sorgenti della connessione salvata in SFA (IP, rack, slot, endpoint, certificati, parametri ADS o MQTT). Per prepararle si avvia una volta in modalità Space Fieldbus Analyzer, si configura la connessione nel popup Connessione e ci si connette (la configurazione viene salvata). Nella richiesta, `sourceId` è il protocollo (`s7`, `ads`, `opcua`, `mqtt`) oppure un **alias** definito nelle impostazioni dell'API (ad esempio `PLC01=s7`). Una sorgente viene aperta solo quando una sessione la usa e chiusa quando l'ultima sessione termina.

## Pagina di monitoraggio

![live_api_monitor](Pagina della modalità Live Acquisition API con due sessioni attive, i dettagli di una sessione e le richieste ricevute.)

| N. | Elemento |
|---|---|
| 1 | **Stato dell'ascolto**: indirizzo e porta su cui l'API riceve le richieste (in rosso se la porta non è disponibile). |
| 2 | **Riepilogo**: sessioni attive, richieste ricevute, sessioni create dall'avvio, sorgenti aperte con il loro stato, memoria del PC. |
| 3 | **Sessioni attive**: identificativo della sessione, `requestId` del sistema esterno, indirizzo del client, sorgente, numero di segnali, periodo richiesto → effettivo, stato (`starting`, `connecting`, `streaming`), stato del WebSocket, ultima sequenza, campioni inviati e scadenza (TTL). |
| 4 | **Dettagli** e **Termina**: apre i dettagli della sessione oppure la chiude (come una DELETE del sistema esterno). |
| 5 | **Dettagli della sessione**: destinazione WebSocket, ultimo errore, campioni scartati e riconnessioni; per ogni segnale indirizzo, tipo, ultimo valore con qualità e ora, campioni inviati; registro degli eventi (WebSocket connesso, `stream_started`, errori di lettura, `stream_stopped`). |
| 6 | **Richieste ricevute**: le ultime 300 richieste REST con ora, client, metodo, percorso, codice HTTP, durata, esito (`ok` o codice di errore, ad esempio `unauthorized` o `signal_address_invalid`) e `requestId`. |

Sotto le sessioni attive, la tabella **Sessioni terminate** riporta le ultime 50 sessioni con il motivo della fine (`requested`, `expired`, `stream_target_unavailable`, `service_stopped`) e l'ultimo errore.

## Prova sul PC

![live_api_test](Prova sul PC: la pagina fa da sistema esterno e riceve i campioni su un ricevitore interno.)

| N. | Controllo |
|---|---|
| 1 | **Sorgente**: protocolli della connessione salvata e alias. |
| 2 | **Indirizzo** del segnale, come lo scriverebbe il sistema esterno (S7 `DB200.DBD56`, ADS `MAIN.Temperatura`, OPC UA `ns=2;s=Temperatura`, MQTT `impianto/motore#/carico`). |
| 3 | **Tipo** del dato (BOOL, BYTE, WORD, DWORD, INT, DINT, REAL e gli altri tipi S7). |
| 4 | **Periodo** richiesto in millisecondi. |
| 5 | **TTL**: durata massima della sessione in secondi. |
| 6 | **START** / **STOP** (e **Pulisci grafico**). |
| 7 | **Stato della prova**: sessione, stato, WebSocket, campioni ricevuti, sequenza (con eventuali salti) e ultimo valore. |
| 8 | **Grafico** dei campioni ricevuti. |
| 9 | **Registro del ricevitore**: connessione, `stream_started`, primo campione, eventuali `signal_status` e `stream_stopped`. |

La prova segue lo stesso percorso di un sistema esterno (richiesta REST, acquisizione, WebSocket in uscita): se la prova funziona ma un sistema esterno non riceve dati, il problema è nella rete, nel firewall o nell'elenco degli host autorizzati.

## Connessione per i sistemi esterni e impostazioni

![live_api_settings](Indirizzi da comunicare al sistema esterno, chiave API e impostazioni.)

| N. | Controllo |
|---|---|
| 1 | **Indirizzi** dell'API da comunicare al sistema esterno (uno per scheda di rete del PC). |
| 2 | **Chiave API**: va inviata in ogni richiesta con l'intestazione `X-Api-Key` (oppure `Authorization: Bearer`). **Mostra** e **Copia** la rendono disponibile. |
| 3 | **Nuova chiave**: genera una nuova chiave; la precedente non vale più. |
| 4 | **Sorgenti** della connessione salvata in SFA e alias definiti. |
| 5 | **Porta** di ascolto (predefinita 5180). |
| 6 | **Indirizzo di ascolto**: `0.0.0.0` per tutte le schede di rete, oppure un indirizzo del PC. |
| 7 | **Host autorizzati per lo streamTarget**: nomi o indirizzi verso cui SFA può aprire il WebSocket (uno per riga). Una richiesta verso un altro host viene rifiutata con `invalid_stream_target`; questo PC è sempre autorizzato. |
| 8 | **Alias delle sorgenti**: ad esempio `PLC01=s7`. |
| 9 | **Richiedi la chiave API**: da lasciare attivo; senza chiave chiunque in rete può leggere i segnali del PLC. |
| 10 | **Salva impostazioni**: se cambiano porta o indirizzo l'ascolto si riavvia; le sessioni attive proseguono. |

Le altre impostazioni sono: sessioni contemporanee (8), segnali per sessione (64), periodo minimo (10 ms), TTL massimo (24 ore), riconnessione (attesa iniziale 1 s raddoppiata a ogni tentativo fino a 30 s, 10 tentativi, 120 s in tutto) e intervallo dei messaggi `samples` (100 ms). Le impostazioni sono salvate in `live_api.json` nella cartella dati; le sessioni no.

Al primo avvio con l'indirizzo `0.0.0.0` Windows può chiedere di consentire l'accesso di rete all'applicazione: va consentito per la rete in cui si trova il sistema esterno, oppure va aperta la porta nel firewall.

## Uso da parte del sistema esterno

| Richiesta | Risposta |
|---|---|
| `POST /api/v1/live-sessions` | **201 Created** con `sessionId`, `status` (`starting`), `createdAt`, `expiresAt`, `requestedSamplingPeriodMs` ed `effectiveSamplingPeriodMs`. Il 201 indica che la sessione è accettata: subito dopo SFA apre il WebSocket verso `streamTarget.url`. |
| `GET /api/v1/live-sessions/{sessionId}` | Stato della sessione, con `streamConnected`. |
| `DELETE /api/v1/live-sessions/{sessionId}` | **204 No Content**: acquisizione fermata, `stream_stopped` inviato, WebSocket chiuso, risorse liberate. |
| `GET /api/v1/live-sessions` | Elenco delle sessioni attive (diagnostica). |
| `GET /api/v1/health` | Stato del servizio (senza chiave). |

Esempio di richiesta:

```
POST http://<PC-Edge>:5180/api/v1/live-sessions
X-Api-Key: <chiave>
{"requestId":"test-8f21","sourceId":"PLC01",
 "signals":[{"signalId":"pyrometer-entry-stand-1","address":"DB200.DBD56","dataType":"REAL","unit":"°C"}],
 "samplingPeriodMs":250,"ttlSeconds":1800,
 "streamTarget":{"protocol":"websocket","url":"ws://TEST-PC:8080/api/live-streams/test-8f21"}}
```

Sul WebSocket arrivano, nell'ordine: `stream_started` (con `schemaVersion` 1, periodo effettivo e segnali), poi messaggi `samples` con un `sequence` progressivo e gruppi di campioni (`signalId`, `timestamp` UTC ISO 8601 assegnato dal PC di acquisizione, `value`, `quality` `good`/`uncertain`/`bad`), eventuali `signal_status` quando un segnale non si legge più o torna valido, e infine `stream_stopped` con il motivo.

| Voce | Comportamento |
|---|---|
| Periodo | Se è più breve del periodo minimo viene alzato; la risposta indica sempre il periodo richiesto e quello effettivo. I campioni sono inviati al periodo della sessione anche quando la sorgente legge più spesso per un'altra sessione. |
| Errori di lettura | Un segnale che non si legge produce campioni `bad` senza valore e un `signal_status` con il codice `acquisition_failed` (o `source_unavailable` se la sorgente è disconnessa): non viene mai ripetuto l'ultimo valore valido. |
| WebSocket perso | SFA ritenta la connessione (attesa crescente) e, quando riesce, invia un nuovo `stream_started` e riparte con la sequenza; i campioni del periodo di disconnessione non vengono conservati. Se la destinazione resta irraggiungibile oltre i limiti, la sessione termina con `stream_target_unavailable`. |
| TTL | Alla scadenza: `stream_stopped` con motivo `expired`, WebSocket chiuso, sessione eliminata. |
| Errori REST | Sempre nel formato `{"status":"error","error":{"code","message","retryable"}}`. Codici: `unauthorized` (401), `invalid_request`, `signal_address_invalid`, `signal_type_unsupported`, `sampling_period_unsupported`, `invalid_stream_target` (400), `source_not_found`, `session_not_found` (404), `session_limit_reached` (429), `source_unavailable` (503), `internal_error` (500). |
| Sola lettura | L'API legge e basta: non esistono comandi di scrittura, forzatura, setpoint o reset verso il PLC. |
| Persistenza | Le sessioni esistono solo in memoria e non modificano la configurazione di SFA (trend, segnali, workspace, registrazioni). Restano il log tecnico e il registro eventi. |

La modalità non sostituisce la sicurezza di rete: la chiave viaggia in chiaro con `http`/`ws`. Va usata in una rete di impianto controllata.
