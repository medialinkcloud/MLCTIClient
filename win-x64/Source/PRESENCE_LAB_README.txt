MLCTI R7.3 PRESENCE - CANDIDATA LAB (NON DISTRIBUIRE)

- Usa R7.3 BLF sidebar + correzione colonne come base, nessuna modifica al licensing.
- Avatar in alto a destra, cinque stati: Available, Away, Chat, DND, Unavailable.
- Seleziona foto profilo dal menu: file locale, nessun upload.
- Lettura getSIPAccount via API HTTPS UCM; refresh a 90 secondi, massimo 25 interni.
- Stato non leggibile = sconosciuto (non inventare presenza).
- updateSIPAccount scrive solamente presence_status del proprio interno.
- Se UCM segnala need_apply=yes, non eseguire applyChanges in automatico: potrebbero esserci modifiche PBX pendenti non correlate. Verificare prima su laboratorio.
- DND presenza NON equivale sempre al DND locale del telefono.
- Nessun cambio agli installer, alle licenze o alla configurazione server.

TEST DA ESEGUIRE:
  Windows10-11-x64: dotnet build Windows10-11-x64/MedialinkCTIClient.csproj -c Release -f net10.0-windows
  Windows7: dotnet build Windows7-SP1-x86/MedialinkCTIClient.csproj -c Release -f net48
  Dopo la build, verificare lettura stati UCM e modifiche su interno LAB.
