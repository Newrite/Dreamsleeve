Scriptname DreamsleeveClient Hidden
{Dreamsleeve client API for Papyrus. Requires Dreamsleeve.Client.dll.
See docs/DreamsleeveModApiRu.md in the Dreamsleeve repository.}

; =======================================================
; ANNOUNCEMENT KINDS
; =======================================================

; A general notice.
int Function KIND_ANNOUNCEMENT() global
    return 0
EndFunction

; A game event: a death, an achievement, a discovery.
int Function KIND_EVENT() global
    return 1
EndFunction

; =======================================================
; RESULT CODES
; numArg of the mod event "Dreamsleeve_AnnouncementResult";
; strArg is the source passed to PostAnnouncement.
; =======================================================

int Function RESULT_PUBLISHED() global
    return 1
EndFunction

int Function RESULT_NOT_CONNECTED() global
    return 2
EndFunction

int Function RESULT_REJECTED() global
    return 3
EndFunction

int Function RESULT_BUSY() global
    return 4
EndFunction

int Function RESULT_RATE_LIMITED() global
    return 5
EndFunction

int Function RESULT_FAILED() global
    return 6
EndFunction

; =======================================================
; NATIVE FUNCTIONS
; =======================================================

; Asks the server to publish asText in the system channel (the announcements tab).
; aiKind: KIND_ANNOUNCEMENT() or KIND_EVENT().
; asSource: the name of your mod, one line; shown next to the text.
; True means queued, not published: register for the mod event
; "Dreamsleeve_AnnouncementResult" to learn the outcome. False: not queued
; (no connection, text or source not UTF-8, a multiline source, a full
; queue); the reason is written to DreamsleeveClient.log.
bool Function PostAnnouncement(string asText, int aiKind, string asSource) global native

; True while a server session is ready.
bool Function IsConnected() global native

; Version of this script API; 1 for the functions above.
int Function GetApiVersion() global native
