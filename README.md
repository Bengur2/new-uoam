# new UOAM

Nástupce staré UO Auto-Map: čte `.mul`/`.uop` soubory klienta přímo, zobrazuje mapu a pozici
hráče s výrazně nižší latencí. Viz `docs/UOP_FORMAT_NOTES.md` pro empiricky ověřené detaily
formátu map souborů.

## Instalace pro hráče

Web s popisem a ovládáním: **https://bengur2.github.io/new-uoam/**. Stáhni
[NewUOAM-Setup.exe](https://github.com/Bengur2/new-uoam/releases/latest/download/NewUOAM-Setup.exe)
a spusť ho (případně zip z [Releases](https://github.com/Bengur2/new-uoam/releases/latest),
rozbalený mimo `Program Files`). .NET instalovat není potřeba. Nové verze mapa nabídne sama při spuštění (nebo **Mapa > Zkontrolovat aktualizace…**).
Markery a nastavení zůstanou. Vydávání verzí a pravidla kompatibility jsou v `docs/RELEASE.md`.

## Co mapa posílá ven (soukromí)

- **Paměť klienta jen čte**, a jen pozici tvé postavy (X/Y/Z). Nic do hry nezapisuje, nic do ní
  nevkládá. Jméno postavy bere z titulku okna. Zprávy do hry jdou přes rozhraní UOAssist, které
  Orion Assistant k tomu nabízí.
- **Relay server dostane data, jen když se připojíš do místnosti** (Online > Připojit k mapě):
  heslo místnosti, tvoje zobrazované jméno a barvu, pozici, chat, sdílené markery, paniku,
  sdílený marker a track reporty. Vidí je jen lidé ve stejné místnosti. Server si na disk ukládá
  jen seznam místností (jméno + heslo). Pozice, chat i markery drží jen v paměti. Do provozního
  logu zapisuje příchod/odchod (jméno, místnost, IP adresa), položení sdíleného markeru, paniku a
  track report (kdo, kolik jmen, kde). **Obsah chatu ani průběžné pozice nezapisuje.**
- **Kontrola aktualizací** = jeden dotaz na GitHub (`releases/latest/download/update.json`) při
  spuštění.
- Nic dalšího. Celý kód je v tomhle repu, včetně serveru (`src/NewUOAM.RelayServer`).

## Struktura

- `src/NewUOAM.MapData` — čtení `.mul`/`.uop` map (land tiles), `radarcol.mul` barvy, parser
  markerů (`.map` UOAM formát + `.csv`). Bez závislosti na UI, testovatelné samostatně.
- `src/NewUOAM.Positioning` — `IPositionProvider` abstrakce + tři varianty zdroje pozice hráče.
- `src/NewUOAM.App` — WPF aplikace (vykreslení mapy, ovládání, live pozice).
- `src/NewUOAM.MemoryScanner` — interaktivní nástroj na živé hledání offsetů v paměti hry (Cheat
  Engine styl), použitý k reálnému nalezení offsetů pro Variantu B2 (viz níže i CLAUDE.md).
- `src/NewUOAM.RelayServer` — samostatný konzolový relay server pro multiplayer (viz níže).
  Nezávislý na `NewUOAM.App`, jede jako vlastní proces - buď lokálně, nebo na VPS.
- `src/NewUOAM.UoaBridge` — malý pomocný proces pro příkazy psané ve hře (`-c`, `-panic`, viz
  „Příkazy ve hře“). Spouští ho appka sama, jeden na každého klienta.
- `src/NewUOAM.Updates` — automatické aktualizace (podepsaný manifest, stažení, výměna souborů).
- `tools/NewUOAM.ReleaseTool` + `tools/Release/release.ps1` — sestavení a vydání verze pro hráče
  (viz `docs/RELEASE.md`).
- `tools/OrionScripts/PositionFeed.oajs` — skript pro Variantu A (běží uvnitř OrionUO). Zdrojová
  kopie v repu — **skutečná** kopie, kterou OrionUO čte, je v `C:\Orion Launcher\OA\` (viz níže).
- `tools/UopProbe` — validační/diagnostický nástroj použitý při vývoji (ne součást appky).

## Rozhraní appky

Veškeré ovládání (cesta ke klientovi, Varianta A, Multiplayer, Markery) je v **postranním panelu
nastavení** vlevo — sbalitelném ikonou ☰ vlevo nahoře nad mapou. Panel má pevnou šířku nezávislou
na šířce okna (se svým vlastním posuvníkem, pokud se obsah nevejde na výšku), takže na rozdíl od
dřívějšího horního toolbaru se při zúžení okna žádné tlačítko neschová. Stav (otevřeno/zavřeno) se
pamatuje mezi spuštěními. Mapa vyplňuje zbytek okna a je striktně ořezaná na svoje hranice
(`ClipToBounds`) — markery, ostatní hráči ani vlastní pozice tak nemůžou vizuálně "prosvítat" do
panelu nebo stavového řádku, i když se pozicují blízko okraje.

## Zdroje pozice hráče (`IPositionProvider`)

| Varianta | Stav | Princip | Riziko pro stabilitu klienta |
|---|---|---|---|
| A — Orion script + UDP | **Hotovo, v UI skryté** (od 24. 9. 2026, backend zůstává) | `PositionFeed.oajs` běží v OrionUO, čte `Player.X/Y/Z/Map` a posílá UDP na `127.0.0.1:27974` | Skript běží v procesu klienta (oficiální OrionUO API, ne raw injection) |
| B1 — Packet proxy | **Odloženo** (funkční kód, UI vypnuté) — viz níže | Lokální proxy mezi `client.exe` a serverem; přepíše jen login-redirect packet (`0x8C`), zbytek jen čte, nikdy nemění | Žádné — nic se neinjektuje do klienta, chyba v parsování nanejvýš přestane hlásit pozici |
| B2 — Process memory | **Hotovo, funkční (živě ověřeno)** | `ReadProcessMemory` z `OrionUO64.exe` přes stabilní ukazatel nalezený živou seancí přes `NewUOAM.MemoryScanner` | Žádné (jen čtení zvenčí), ale křehké na aktualizace klienta |

**B1 živě otestováno proti Dark Paradise (Sphere) — funkční blokátor nalezen, ne bug:**
login-hop je čitelný plaintext (potvrzeno hex dumpem: `0xEF` login seed, doslova jméno účtu
v datech, `0xA8` server list s `Dark Paradise`), ale **herní server po redirectu vyžaduje starou
"login encryption" vrstvu** — klient tam pošle jen 4 holé bajty (raw seed, ne `0xEF` obálka) a
další provoz je nečitelný. Přesně to řeší checkbox "remove encryption" u Razor/UOSteam. Kód
(`NewUOAM.Positioning.Providers.PacketProxyPositionProvider` a vše v `PacketProxy/`) zůstává
funkční a otestovaný (syntetické testy + reálné sockety + živý test proti Dark Paradise dostal
proxy až do bodu, kde jasně vidíme šifrovanou vrstvu) — jen je to teď rozhodnutí "další krok by
byl implementovat dešifrování", ne oprava chyby. UI (tlačítka Start/Stop proxy + log panel) je
zakomentované v `MainWindow.xaml`/`MainWindow.xaml.cs`, ne smazané.

## Jak vyzkoušet Variantu A

1. Spusť `dotnet run --project src/NewUOAM.App` (nebo build + spustit `.exe`).
2. V appce zadej cestu ke složce klienta (např. `C:\Moria` nebo DP klient) a klikni **Načíst mapu**.
3. Zkopíruj `tools/OrionScripts/PositionFeed.oajs` do `C:\Orion Launcher\OA\` (skutečná složka,
   ze které Orion Launcher v tomto setupu čte skripty — vidět v poli "Scripts from folder" na
   záložce **Scripts**; NENÍ to `tools/OrionScripts` v repu ani `OA\DP\`/`OA\TEMP\`, to jsou jen
   pracovní/dema kopie).
4. V OrionUO na záložce **Scripts** vyber `PositionFeed.oajs` ze seznamu vlevo dole. **Důležité:**
   OrionUO nabízí ke spuštění jen pojmenované top-level funkce (vidět v dropdownu "Handling"
   vlevo nahoře, stejně jako u vašich ostatních skriptů typu `BagDump`) — proto je celé tělo
   skriptu zabalené v `function PositionFeed() { ... }`, ne jen holé příkazy. V dropdownu vyber
   `PositionFeed` a klikni zelené ▶ **Start**.
5. V appce klikni **Start Orion feed** — po prvním pohybu postavy by se měla mapa vycentrovat
   na hráče a ukázat červený marker.

Skript čte pozici každých **20 ms** (`POLL_MS` v `PositionFeed.oajs`, dřív 50 ms) — jde jen
o lokální čtení `Player.X/Y/Z()` z paměti klienta, nic se tím neposílá na server, takže na
fastwalk/anti-cheat to nemá žádný vliv. Po úpravě skriptu ho v Orionu musíš **zastavit a znovu
spustit** (Orion nenačte změnu na disku do už běžícího skriptu automaticky).

## Jak vyzkoušet Variantu B2 (čtení paměti klienta)

1. Spusť a přihlas se do hry přes OrionUO (proces `OrionUO64.exe`).
2. V appce v sekci **VARIANTA B2 (PROCESS MEMORY)** vyber v rozbalovacím seznamu svého klienta
   (pokud jich běží víc najednou, uvidíš je všechny s titulkem okna a PID — ⟳ obnoví seznam).
3. Klikni **Start**.

Na rozdíl od Varianty A (čeká na UDP pakety od skriptu) appka čte pozici přímo z paměti hry
50× za sekundu bez závislosti na jakémkoliv Orion skriptu. Cesta k datům (nalezená živou
seancí 2026-09-23, viz CLAUDE.md pro celý postup):
`OrionUO64.exe (základ modulu) + 0x3796EF8` → ukazatel → `+0x28` = X, `+0x2C` = Y, `+0x41` = Z.

**Známá omezení:**
- **Facet je natvrdo Felucca (0)** — DP shard v praxi jinou mapu nepoužívá, takže se offset pro
  facet zatím nehledal. Kdyby se to řešilo pro shard s víc mapami, potřeba další seance s
  `NewUOAM.MemoryScanner` (přejít na jinou mapu a sledovat, co se změní).
- **Jméno postavy** se nečte z paměti (hledání v paměti bylo příliš nejednoznačné — stovky
  nesouvisejících výskytů v chatu/UI textech), místo toho appka použije titulek okna hry
  (`"Bodhi (Dark Paradise)"` → `"Bodhi"`). Titulek se občas krátce přepne na jiné okno klienta
  (třeba dialogové okno) — v tu chvíli může appka krátce ukázat špatné jméno, ale samotná
  pozice (X/Y/Z) tím není ovlivněná.
- Offsety platí jen pro tuhle konkrétní verzi klienta — po aktualizaci OrionUO je může být
  potřeba znovu najít (viz níže).

### Když se offsety rozbijí (aktualizace klienta)

Pokud Varianta B2 přestane hlásit rozumné hodnoty (nebo appka spadne s chybou čtení paměti),
je potřeba znovu najít offsety živou seancí:

```bash
dotnet run --project src/NewUOAM.MemoryScanner
```

Postup uvnitř nástroje (podrobně viz CLAUDE.md):
1. `attach OrionUO64` — připojí se k běžícímu klientovi.
2. `scan <tvoje X>` — první scan podle aktuální X souřadnice.
3. Popojdi kousek, `next <nová X>` — zúží kandidáty. Opakuj, dokud nezůstane málo kandidátů.
4. `near <adresa>` — hex dump okolí, najdi Y/Z podle toho, co se mění spolu s tvým pohybem.
5. `findptr <adresa objektu>` (případně `findptrrange <od> <do>`) — najde stabilní ukazatel
   uvnitř samotného modulu hry, ne v (mezi restarty nestabilní) heap paměti.
6. Nové offsety zapiš do `ProcessMemoryPositionProvider.cs` (`PointerOffsetFromModuleBase`,
   `OffsetX`/`OffsetY`/`OffsetZ`).

## Znovu zapnout Variantu B1 (až budeme řešit dešifrování herního serveru)

1. V `MainWindow.xaml` odkomentovat blok `<!-- Varianta B1 (packet proxy) ... -->` a
   `<!-- Log panel ... -->`.
2. V `MainWindow.xaml.cs` odkomentovat `StartProxyButton_Click`/`StopProxyButton_Click`/
   `ClearLogButton_Click` a `_proxyProvider` pole, vrátit `Log()` na verzi co píše i do
   `LogTextBox` (viz git historie/komentáře na místě).
3. Postup spuštění a co appka hlásí je zdokumentováno v `CLAUDE.md` (sekce B1) — adresa
   reálného serveru `178.238.38.119:2593` je vytažená z `C:\Orion Launcher\Config\Profiles.xml`
   (profil `DP` → tvůj účet), lokální login port defaultně `2593`.

## Multiplayer (relay server) — víc hráčů na jedné mapě

Samostatná vrstva nad `IPositionProvider` - nezávislá na tom, odkud appka bere VLASTNÍ pozici
(Varianta A/B1/B2), jen ji předává dál a zobrazuje ostatní hráče. Protokol i klientská část jsou
v `NewUOAM.Positioning/Relay/` (`RelayProtocol.cs`, `RelayMultiplayerClient.cs`), server je
samostatný projekt `src/NewUOAM.RelayServer`.

**Jak to funguje:** appka pošle svoji pozici (jakmile ji zná - z lokálního position provideru)
na relay server přes UDP, ten ji rozešle všem ostatním připojeným klientům, kteří ji zobrazí jako
barevný bod + jméno na mapě (jen pokud jsou na stejném facetu, jaký má appka zrovna zobrazený).
Žádná autentizace/šifrování - kdokoliv, kdo zná adresu:port serveru, může nahlásit falešnou pozici
nebo číst seznam hráčů. V pořádku pro malou skupinu kamarádů, ne pro veřejný/nedůvěryhodný server.

**Spuštění serveru:**
```bash
dotnet run --project src/NewUOAM.RelayServer -- --port 27980 --bind 0.0.0.0
```
`--port`/`--bind` jsou nepovinné (defaulty `27980`/`0.0.0.0` = poslouchá na všech rozhraních).
Stejný build/proces jede beze změny jak lokálně na tvém PC, tak na VPS - jediný rozdíl je, jakou
adresu pak hráči zadají do appky (sekce "Multiplayer" v postranním panelu nastavení, textbox `host:port`, tlačítko
**Připojit**). UDP tady záměrně, ne TCP: klient vždy iniciuje spojení, takže to funguje přes
běžný domácí NAT/firewall stejně jednoduše jako herní klient sám - žádné port-forwardování na
klientské straně není potřeba, jen na straně serveru (VPS má typicky žádný NAT řešit netřeba,
lokální firewall pravidlo stačí).

Appka posílá svoji pozici na server nejvýš jednou za 50 ms (`RelayProtocol.ClientReportIntervalMs`,
původně 500 ms, pak 150 ms — po živém testu se to pořád zdálo "pomalé" stejně jako u staré UOAM,
sníženo) - i tak je to nad rámec toho, co lokální Orion feed vůbec dokáže dodávat čerstvé (ten
běží na ~20 ms), takže nejde o plýtvání. Tohle zároveň slouží jako keepalive (server odpojí
"session" bez zprávy 15 s, `SessionTimeoutSeconds`). Marker ostatních
hráčů mezi jednotlivými zprávami plynule "klouže" (stejná animační technika jako u vlastní pozice,
ale vlastní kratší 60ms doba trvání, `RemoteAnimationDurationMs` — musí držet krok s tím 50ms
intervalem, jinak by ta animace latenci naopak vracela zpátky), ne skáče. Adresa serveru i vlastní
zobrazované jméno se pamatují mezi spuštěními appky stejně jako cesta ke klientovi, ale
**nepřipojuje se automaticky** při startu (na rozdíl od mapy/UDP feedu) - připojení k síťové službě
je vědomá akce, ne auto-start.

- **Zobrazované jméno** — textbox "zobrazované jméno" vedle adresy serveru. Když je vyplněný,
  posílá se ostatním hráčům místo jména postavy z Orion feedu (např. si můžeš appku nechat
  zobrazovat jako "GG" bez přejmenování postavy ve hře). Prázdné pole = použije se jméno postavy.
- **Šipka pro hráče mimo viditelnou část mapy** — když je někdo z připojených hráčů zrovna
  odzoomovaný/scrollnutý mimo tvůj aktuální výřez mapy (typicky když má jiný přiblížený pohled),
  appka místo tečky ukáže šipku u okraje mapy (s malým odsazením od kraje), otočenou směrem k jeho
  skutečné pozici, i se jménem. Jakmile se dostane zpátky do viditelné části (posunem/oddálením
  mapy), šipka se zase změní na normální tečku.

- **Barva** — tlačítko s barevným čtverečkem pod zobrazovaným jménem otevře mřížku 72 barev
  (všechny dost světlé, aby byly čitelné na tmavé mapě i v chatu) a pole pro vlastní barvu
  `#RRGGBB` (příliš tmavou odmítne). Ostatním se v ní ukáže tvůj čtvereček/šipka a jméno na mapě a
  tvoje jméno i text v chatu. Změna se projeví hned, i během připojení. Hráči se starší verzí
  appky se zobrazují azurově.
- **Okamžité ohlášení příchodu/odchodu** — appka se serveru ohlásí hned po kliknutí na Připojit
  (i bez běžícího sledování pozice), server jí hned pošle pozice ostatních v místnosti a ostatním
  oznámí, že ses připojil(a). Odpojit to ostatním oznámí hned (dřív až po 15 s). Týká se to vždy
  jen dané místnosti.
- **Chybové kódy při připojení** (okno s vysvětlením + stavový řádek):
  - `101` neplatná adresa serveru (čeká se `host:port`)
  - `102` chybí heslo místnosti
  - `103` neviditelné jméno — jen mezery, tabulátory, zero-width znaky apod. (úplně prázdné pole je
    v pořádku, použije se jméno postavy)
  - `104` připojení selhalo (chyba sítě/socketu)
  - `105` nepodařilo se připojit k mapě, kontaktujte admina mapy (schválně neříká proč — ve
    skutečnosti neexistující/špatné heslo místnosti)
  - `106` relay server neodpovídá (špatná adresa, server nejede, firewall)
  - `107` toto jméno už v místnosti někdo používá. Jména jsou jedinečná v rámci místnosti, v jiné
    místnosti stejné jméno být může. Pozor: dvě okna mapy na jednom PC sdílí nastavení, takže
    mají stejné zobrazované jméno, v jednom ho změň.

### Místnosti (rooms) — oddělené skupiny hráčů na jednom serveru

Jeden relay server může hostit víc oddělených skupin kamarádů zároveň, aniž by na sebe navzájem
viděly. Místnost má **jméno** (jen popisek pro admina) a **heslo** (to, co se skutečně používá k
připojení). Hráč, který se připojí se špatným/neexistujícím heslem, prostě nikoho nevidí a ani
sebe nikomu neukáže — server takové hlášení tiše zahodí, nezakládá pro něj žádnou "session".

- **Připojení jako hráč**: textbox **Heslo místnosti** vedle adresy serveru a zobrazovaného jména
  — musí odpovídat heslu místnosti, kterou už admin založil. Bez vyplněného hesla appka Připojit
  odmítne.
- **Admin okno**: tlačítko **Admin…** vedle Připojit/Odpojit otevře samostatné okno — zadáš adresu
  serveru a **admin heslo** (jiné než hesla místností, nastavuje se serveru při spuštění přes
  `--admin-password`, viz níže). Umí:
  - **Vytvořit místnost** (jméno + heslo).
  - **Obnovit seznam** existujících místností — vidíš jen jména, nikdy hesla, seznam připojených
    hráčů ani jejich pozice.
  - **Smazat vybranou místnost** — vyber místnost v seznamu, klikni **Smazat vybranou místnost**,
    potvrď dialog. Kdokoliv v ní byl zrovna připojený, se rovnou odpojí (dostane zprávu, že ho
    odpojil admin) a místnost zmizí ze seznamu. Nevratné — smazané heslo už nikde nezůstává.
- Místnosti se ukládají do `rooms.json` vedle binárky serveru, takže přežijí restart služby.
- **Spuštění serveru s admin rozhraním**:
  ```bash
  dotnet run --project src/NewUOAM.RelayServer -- --port 27980 --bind 0.0.0.0 --admin-password <tajne-heslo>
  ```
  Bez `--admin-password` server běží normálně (reporty/broadcasty fungují), ale admin příkazy
  (vytvoření i výpis místností) vrací chybu `ADMIN_DISABLED`.

### Chat v místnosti — žádné logování, jen živá komunikace

Klikni myší do mapy (dá jí to focus) a začni psát — automaticky se otevře chat okno, kam píšeš a
zároveň v něm vidíš, co píšou ostatní ve stejné místnosti. Funguje, ať už je heslo místnosti
správné nebo ne (appka to stejně nepozná, viz sekce Místnosti výše) — pokud heslo nesedí, prostě
nikoho neuslyšíš a nikdo neuslyší tebe.

- **Okno je resizovatelné**, prvky (seznam zpráv, vstupní pole) se přizpůsobují velikosti okna —
  seznam zpráv roste/zmenšuje se s oknem, vstupní řádek si drží svou výšku. Okno má nastavené
  minimum (260×180 px), aby se nedalo zmenšit na nepoužitelnou velikost.
- **Nic se nikde neukládá** — ani appka, ani relay server zprávy nikam nezapisují. Zavřeš-li chat
  okno, historie zmizí; při dalším otevření (klik do mapy + psaní) začíná úplně od nuly.
- Enter nebo tlačítko **Odeslat** zprávu pošle. Zprávy jsou omezené na 400 znaků.
- **Chat i ve hře** (zaškrtávátko „Zobrazovat chat mapy ve hře“, výchozí zapnuto) — zprávy a
  hlášky o připojení/odpojení se ukážou i v okně Ultimy vlevo dole jako systémové zprávy
  (`[Mapa] Jméno: text`), vidíš je jen ty. Stejně jako stará UOAM přes rozhraní UOAssist, které
  nabízí Orion Assistant — do klienta se nic nezapisuje. Píše se do klienta sledovaného ve
  Variantě B2 (bez ní do jediného přihlášeného klienta, pokud běží jen jeden). Ve hře jsou zprávy
  bez diakritiky (herní font ji neumí) a jednou barvou (zelená).
- Zprávy jsou obarvené barvou odesílatele. Když se někdo do místnosti připojí nebo z ní odpojí,
  objeví se šedá kurzívou psaná řádka.
- **Chat se nikdy neotevře sám** — ani kvůli příchozí zprávě (ta se ukáže ve hře), otevíráš ho
  jen psaním do mapy. Zprávy, které přijdou při zavřeném chatu, se v něm neuchovají.
- **Dvojklik do seznamu zpráv** — kompaktní režim: zmizí horní lišta okna, chat je vždy navrchu
  (nezávisle na tom, jestli je navrchu mapa), zprávy, pole pro text a tlačítko zůstanou. Okno
  přesuneš tažením za seznam zpráv, velikost měníš úchytem vpravo dole. Další dvojklik ho vrátí.
- Chat je vypnutý (netriguje se), dokud nejsi připojený přes tlačítko **Připojit** v sekci
  Multiplayer.

### Příkazy ve hře (jako stará UOAM)

Píšou se přímo do herního chatu v Ultimě. Orion Assistant je zachytí, takže je postava neřekne
nahlas, a pošle je mapě:

| Příkaz | Co udělá |
|---|---|
| `-c zpráva` | pošle zprávu do chatu mapy (celé místnosti) |
| `-c jméno>zpráva` | soukromá zpráva jednomu hráči; stačí začátek jména, pokud sedí jen na jednoho |
| `-panic` | zapne Panic!: ostatním v místnosti bliká rámeček mapy, vede k tobě červená tečkovaná čára, tvoje značka bliká, mapa pípá a ve hře se jim nad hlavou každých 10 s ukáže „PANIC! jméno potřebuje pomoc! -> směr, N tiles“ |
| `-unpanic` | vypne Panic! — i když ho zapnul někdo jiný |
| **mezerník** v mapě | zapne Panic!, nebo vypne ten, který v místnosti běží (klikni nejdřív do mapy) |

- V místnosti může být zapnutý **jen jeden Panic!** najednou. `-panic` jiného hráče ten
  předchozí nahradí a vypnout ho může kdokoliv v místnosti (kdyby ho někdo zapomněl zapnutý).
  Když panikařící hráč odejde z mapy, panic zmizí s ním.
- Když spoluhráč zapne Panic!, tvoje mapa na něj rychle přeletí, 2 s ho ukáže a vrátí se zpátky:
  se zapnutým Track Player na tvoji postavu, s vypnutým přesně na výřez, který jsi měl. Když
  mezitím mapu chytneš a potáhneš (nebo přepneš Track Player), zůstane tam, kde ji necháš.
- Text „PANIC! …“ nad hlavou je červený. Dokud panic běží, text „Shared Marker“ se nezobrazuje
  a po skončení panicu se do 10 s vrátí.
- Panic! jde zapnout/vypnout i v menu na pravém tlačítku nad mapou. Když ho máš zapnutý ty,
  svítí na mapě nahoře červené „PANIC!“. Pípání jde vypnout zaškrtávátkem „Zvuk při panice
  spoluhráče“. Mezerník v okně chatu je normální mezera, panic přepíná jen v mapě.
- Místo `--zpráva` ze staré UOAM je `-c zpráva`: řádky začínající `--` Orion Assistant spolkne a
  nikomu je nepředá.
- Zpráva se znakem `>`, jejíž začátek neodpovídá žádnému hráči, se **neodešle** (ve hře přijde
  chyba), aby omylem nešla celé místnosti.
- **Pozor na překlepy:** příkaz, který mapa nezná (např. `-panik`), Orion Assistant nezachytí a
  postava ho řekne nahlas.
- Příkazy fungují v klientu, který sleduje Varianta B2 (nebo v jediném přihlášeném klientu).
  Odpověď přijde vždy do klienta, kde byl příkaz napsán, i když je vypnuté „Zobrazovat chat mapy
  ve hře“.
- Když mapa neběží, hra odpoví „new UOAM nebezi“. Po restartu mapy příkazy fungují dál.
- Jestli nefungují vůbec: klient mohl mít příkazy zaregistrované už dřív jinam (u Orion
  Assistantu platí vždy první registrace, dokud klient běží). Pomůže restart klienta. Záznam
  registrací je v `%LocalAppData%\NewUOAM\uoabridge\bridge.log`.

## Markery (ikony budov/lokací, jako u staré UOAM)

Sekce **Markery** v postranním panelu nastavení: zadej cestu ke **složce** obsahující jeden nebo víc `.map`/`.csv`
marker souborů (starý UOAM formát `+IconName:X Y MapIndex Jméno`, nebo CSV
`x,y,mapindex,name,iconname,color,zoom`) a klikni **Načíst markery** — appka přečte všechny
`.map`/`.csv` soubory přímo v té složce (ne podsložky) a sloučí je do jednoho přehledu. Přesně
tenhle formát používá jak stará UOAM, tak OrionUO ve své "World Map" funkci, takže existující
marker soubory (třeba stažené z komunitních zdrojů, nebo ty, co má tenhle projekt v
`C:\_PERSONAL\Games\DP\Ultima Online DP\*.map` a `Orion Launcher\OrionData\WorldMapExternalMarkers\`)
fungují beze změny.

- **Ikony**: appka má v sobě zabalenou stejnou sadu 199 ikon, jakou používá OrionUO/UOAM
  (`src/NewUOAM.App/Assets/MapIcons/*.png`) — jméno ikony z marker souboru se převede na
  velká písmena bez mezer a spáruje s odpovídajícím souborem (`"armourers guild"` →
  `ARMOURERSGUILD.png`). Pokud pro nějaký typ ikonu nemáme, použije se obecná náhradní (`OTHER.png`).
- **Zobrazit/skrýt**: zaškrtávátko **Zobrazit markery** vedle tlačítka Načíst — vypne/zapne celou
  vrstvu najednou. Jednotlivé markery navíc respektují svůj vlastní příznak viditelnosti ze
  souboru (`+`/`-` na začátku řádku u `.map` formátu) — i se zapnutým zaškrtávátkem se tak
  zobrazí jen ty, co má sám soubor označené jako viditelné (typicky méně husté kategorie jako
  teleporty/zajímavá místa, ne každý jednotlivý pekař ve městě).
- **Název při najetí myší**: každá ikona zobrazí svoje jméno při najetí myší — vlastní
  implementace (`MarkerHoverLabel`), ne nativní WPF tooltip. Nativní tooltip měl reálný problém:
  appka ho při aktivním live UDP feedu (pozice každých ~20 ms) nikdy nestihla zobrazit, ať jsi
  čekal sebedéle (viz CLAUDE.md, "Dispatcher-starvation" bug). Vlastní řešení na tenhle problém
  vůbec nenaráží.
- Markery se zobrazují jen pro facet, který appka zrovna má načtený (stejné pravidlo jako u
  ostatních hráčů). DP shardí marker soubory (`DP Mesta.map`, `DP Dungy.map`) měly původně skoro
  všechny záznamy jen pro Trammel (`MapIndex 1`) — na žádost přidané i duplicitní záznamy pro
  Felucca (`MapIndex 0`, stejné souřadnice), takže teď markery uvidíš na obou facetech. Originály
  souborů jsou zálohované jako `DP Mesta.map.bak` / `DP Dungy.map.bak` ve stejné složce.
- Cesta ke složce s markery se pamatuje mezi spuštěními appky, ale nenačítá se automaticky při
  startu — stejně jako u multiplayeru, stačí to udělat ručně jednou po spuštění.

## Ovládání mapy (platí pro všechny varianty A/B1/B2 — je to na UI vrstvě, ne na position provideru)

- **Kolečko myši** nad mapou = zoom. Krokuje diskrétní seznam úrovní (`MainWindow.ZoomLevels`):
  2–24 pixelů na dlaždici po sudých číslech pro přiblížení (schválně, viz níže), a pro oddálení
  za 2 px/dlaždici hodnoty `2/D` pro D=2..8 (až 0,25 px/dlaždice, tj. víc dlaždic na 1 pixel).
  Obě strany rozsahu drží stejný trik proti "blikání" — viz komentář u `ZoomLevels`.
- **Tlačítko "Pohled: otočený 45° / sever nahoře"** — přepíná mezi klasickým UO izometrickým
  pohledem otočeným o 45° (výchozí, stejná orientace jako stará UOAM a hra samotná — sever
  vpravo nahoře) a rovným "atlasovým" pohledem (sever nahoře). Obě projekce jsou čistě
  na úrovni vykreslování (`MainWindow.WorldToScreen`/`ScreenToWorld`), nemají nic společného
  s tím, odkud appka bere pozici hráče.
- **Pravé tlačítko do mapy** — menu jako ve staré UOAM (zatím dvě položky):
  - **Track Player** — zapnuto (výchozí): mapa sleduje postavu. Vypnuto: mapa zůstane stát,
    posouváš ji tažením levým tlačítkem a tvůj čtvereček se hýbe po mapě. Znovu zapnout = mapa
    skočí zpátky na postavu. (Při vypnutém sledování tažení posouvá mapu i v režimu jen mapy,
    okno pak přesuneš až po zapnutí sledování.)
  - **New Label...** — okno „Edit Label“ jako ve staré UOAM:
    - Name: název.
    - Type: ikona.
    - X/Y: předvyplněné místem kliknutí, dají se přepsat.
    - Land: facet.
    - File: do kterého souboru markerů se label uloží. Výchozí je `NewUOAM Labels.map`; když není
      nastavená žádná složka markerů, založí se `%LocalAppData%\NewUOAM\markers`.
  - **Drop or Pickup Marker** — položí značku (bod, kam chceš běžet): úsečka od tebe k ní a
    vpravo dole vzdálenost v dlaždicích. Zaškrtnutá = značka leží, vybráním ji zase zvedneš.
  - **Drop or Pick Up Shared Marker** (jen při připojení k online mapě) — totéž, ale značku vidí
    všichni v místnosti (každý svoji úsečku a vzdálenost), položit i zvednout ji může kdokoliv
    z místnosti. Při položení se všem ve hře nad hlavou ukáže „Shared Marker“, ASCII šipka a směr k ní
    (např. `Shared Marker /^ North`; šipka míří tam, kam je to na obrazovce klienta, kde je
    sever vpravo nahoře). Dokud značka leží, text se opakuje každých 10 s.
- **Pravé tlačítko na existující marker** → Drop or Pickup (Shared) Marker na jeho místě, **Edit** (stejné okno, jde změnit i soubor) nebo
  **Delete** (s potvrzením). Mění se jen řádek toho markeru, zbytek souboru zůstane byte po
  bytu stejný, včetně starého kódování s diakritikou. Před první změnou souboru vznikne záloha
  `<soubor>.bak`.
- **Dvojklik do mapy** — schová postranní panel nastavení i stavový řádek, okno se stane bezrámečkové a
  Always-on-top (jen samotná mapa, jako přetažený overlay přes hru). Další dvojklik vrátí
  normální okno. V tomto režimu (bez title baru) jde okno přetáhnout myší tažením přímo za mapu
  (jednoduchý klik+táhnutí, `DragMove()`) — dvojklik pořád přepíná režim, jednoduchý klik+táhnutí
  přesouvá okno.
- **Zaškrtávátko "Souřadnice"** — v levém horním rohu mapy ukáže `Facet X,Y` (styl staré UOAM),
  živě se aktualizuje s příchozí pozicí.
- **Zaškrtávátko "Světové strany"** — písmena v rozích mapy. U otočeného pohledu přesně jako
  stará UOAM (W vlevo nahoře, N vpravo nahoře, S vlevo dole, E vpravo dole — sever u otočeného
  pohledu směřuje doprava nahoru), u severního pohledu diagonální rohy (NW/NE/SW/SE). Když jsou
  zapnutá obě zaškrtávátka zároveň, text souřadnic má vlevo dost místa, aby se nepřekrýval
  s písmenem kompasu ve stejném rohu (dřív byly obě přesně na stejném místě).
- Obě zaškrtávátka i cesta ke klientovi se **pamatují mezi spuštěními** appky
  (`%LocalAppData%\NewUOAM\settings.json`).
- **Spuštění jako správce**: appka se při startu sama restartuje s právy správce (UAC dialog). Bez nich Varianta B2 nepřečte paměť klienta, když Orion běží jako správce. Když dialog zrušíš, appka poběží dál bez práv a upozorní na to ve stavovém řádku. Parametr `--no-elevate` restart vypne (pro testy).
- **Auto-load při spuštění**: pokud je uložená platná cesta ke klientovi, appka po startu sama
  načte mapu. UDP naslouchání (Varianta A) se od 24. 9. 2026 automaticky nespouští a Varianta A je
  v UI skrytá. Poloha se bere z Varianty B2, kterou spouštíš ručně.
  Aktuálnost souborů mapy/statics řeší stejná mezipaměť jako jinde (`FacetColorMapDiskCache`,
  kontrola podle data změny + velikosti zdrojových souborů) — pokud se soubory klienta změnily,
  appka je při tomto auto-loadu prostě přebuduje stejně, jako by to udělala po ručním kliknutí.

**Pohyb se renderuje stejně jako ve staré UOAM — vykreslit jednou, posouvat výřez:** appka
kreslí do bufferu o dost většího než viditelný výřez (`EnsureCache`/`RenderRegion` v
`MainWindow.xaml.cs`, defaultně 3× rozměr okna v každém směru — takže plná rezerva na všechny
strany). Dokud se pohybuješ v rámci téhle rezervy, překreslení je čistá kopie paměti (žádný
přepočet barev) — jen když dojde rezerva (nebo změníš zoom/otočení/facet), buffer se jednou
přegeneruje. Sudá čísla u zoomu jsou pořád potřeba (u otočeného pohledu odpovídá 1 dlaždice
posunu `pixelsPerTile/2` px na obrazovce — liché číslo by ořez z bufferu posunulo o půl pixelu a
rozhodilo ho), ale hlavní opravou byl samotný přechod na cache/ořez místo přepočtu za běhu.
Ověřeno v `tools/UopProbe`: syntetická simulace stovek kroků chůze potvrdila, že ořez z bufferu
dává pixelově identický výsledek jako přímé vykreslení, a přes 99 % kroků je čistá kopie (jen
pár přegenerování).

**I s tou opravou šlo poznat, že appka při každém kroku "trhaně" skáče o pár pixelů** (matematicky
korektně, ale nepříjemně na oko) — stará UOAM navíc plynule "klouže". Přidal jsem proto plynulou
~150ms animaci posunu mezi jednotlivými hlášenými pozicemi (`BeginPanAnimation`/`OnAnimationTick`
v `MainWindow.xaml.cs`, přes `CompositionTarget.Rendering`). Nejde o krok zpátky k přepočítávání
za běhu — animuje se jen to, odkud přesně se z **už hotového** bufferu ořízne výřez (viz výše),
takže žádné blikání/rozhozené pixely to nemůže vrátit zpátky, jen to rozloží jeden velký skok na
plynulou sekvenci malých.

**Skutečná příčina "bublání" 1x1 pixelů:** i s animací byl ještě jeden reálný bug — když se buffer
musel za chodu animace přegenerovat (dřív nebo později se to při delší chůzi vždy stane, jakmile
dojde rezerva), přegeneroval se vystředěný na tehdejší (neceločíselnou, uprostřed animace)
pozici. Dva buffery vygenerované na dvou různých necelých pozicích nejsou zaručeně fázově
zarovnané — takže drobné detaily (malé statics, terénní tečky) mohly při přegenerování skočit
o zlomek pixelu. Opraveno: buffer se teď vždy generuje vystředěný na přesnou celočíselnou pozici
hráče, nikdy na neceločíselnou animovanou. Ověřeno v `tools/UopProbe`.

**Skutečná příčina pádu appky po zapnutí UDP:** animace nijak neomezovala, jak daleko od sebe
můžou aktuální a cílová pozice být. Úplně první pozice po startu skočí z výchozího středu mapy na
skutečnou pozici hráče — typicky tisíce dlaždic daleko — a appka se to pokoušela "plynule přejet"
za 150 ms, i když buffer má rezervu jen na jedno okno. Uprostřed animace se pak ořez snažil vzít
kus obrázku, co v bufferu vůbec nebyl → pád. Opraveno dvakrát: skoky větší než ~40 dlaždic se
teď neanimují, jen skočí rovnou; a ořez z bufferu je navíc vždy pojištěný (`Math.Clamp`) do
platného rozsahu bez ohledu na cokoliv, takže appka teoreticky nemůže spadnout ani na jinou
podobnou situaci. Ověřeno naživo přes UI automatizaci + reálný UDP packet reprodukující přesně
tenhle pád.

**Příčina "příliš ostrých/zaoblených" pixelů, co se mění při pohybu (víc vidět při oddálení):**
appka je psaná ve WPF, který kreslí ve "logických" 96 DPI jednotkách — pokud má Windows nastavené
jiné než 100% škálování (na tomhle stroji 125%), WPF náš bitmap ještě jednou přeškáluje na
skutečné DPI monitoru, a tenhle krok používá vlastní (rozostřující) filtr, který se `NearestNeighbor`
na `Image` kontrole netýká. To rozostření se navíc mění podle toho, jak přesně obsah "sedí" na
fyzické pixely obrazovky, a to se s pohybem mění → vypadá to jako "transformace". Opraveno: appka
teď zjišťuje skutečné DPI monitoru (`VisualTreeHelper.GetDpi`) a bitmapu vykresluje rovnou ve
fyzických pixelech, takže WPF už nemá co přeškálovávat.

## Výkon: mapy se předpočítávají při "Načíst mapu" a ukládají na disk

Kliknutí na **Načíst mapu** hned zobrazí facet 0 (ze surových dat) a na pozadí spustí
`UoClientData.PreloadAllColorMapsAsync` — pro každý dostupný facet jednou projde všechny
dlaždice (+ statics na nich), obarví je podle `radarcol.mul` a výsledek uloží jako bitmapu
1 px/dlaždice (`FacetColorMap`). Panning/zoom/rotace pak jen čtou hotové pole místo opakovaného
dekódování syrových bloků — nutné, protože per-pixel vzorkování (kvůli otočenému pohledu) by
jinak volalo dekódování dlaždice pro každý pixel obrazovky při každém překreslení. Na `C:\Moria`
(6 facetů, největší 7168×4096) to i se statics trvá ~21 s a zabere ~270 MB RAM (dřív bez statics
~13 s); dokud běží, appka funguje dál (fallback na přímé čtení), jen o něco pomaleji pro facety,
které ještě nejsou hotové.

**Přesně jak si stará UOAM (a jak jsi chtěl) — jednou vypočítaná mapa se ukládá na disk a
appka ji příště jen načte**, místo aby ji znovu počítala od nuly. Cache leží v
`%LocalAppData%\NewUOAM\colormap-cache\` a appka ji automaticky zahodí a přepočítá, jakmile se
změní některý ze zdrojových souborů (`mapN.mul`/`.uop`, `staidxN.mul`, `staticsN.mul`,
`radarcol.mul`) — hlídá se přesný čas poslední změny + velikost každého souboru, takže update
klienta/shardu se projeví automaticky, nikdy neuvidíš zastaralou mapu. Na `C:\Moria` je rozdíl
enormní: první výpočet facetu ~6,6 s, načtení z cache příště **~0,03 s** (přes 200× rychlejší).

## Statics (budovy, stromy, ploty, ...)

`MulStaticsSource` čte `staidxN.mul` (index bloků) + `staticsN.mul` (samotné položky) — jen
`.mul` varianta, oba testovací klienty (Moria, DP) ji mají přímo, žádné `.uop` komplikace zatím
netřeba. Na tile se kreslí jen **nejvyšší** static (highest Z), stejně jako to dělá herní radar
mapa — barva se bere z `radarcol.mul` na indexu `tileId + 0x4000` (stejná tabulka jako land,
statics/itemy navazují hned za land tiles). Ověřeno na `C:\Moria` (Felucca): 2,76 milionu statics
celkem, oblast kolem Britain má ~735× víc statics na plochu než roh mapy — strukturálně
i prostorově to sedí (`tools/UopProbe`).

## Co chybí (další kroky)

- Varianta B1 — **odloženo**, viz sekce výše. Kód hotový a otestovaný, blokuje ho "login
  encryption" vrstva na herním serveru (ne login serveru). Facet se po loginu pozná ze
  šířky/výšky mapy v `0x1B` (match proti `FacetInfo.Known`) — změna facetu za běhu
  (moongate/sacred journey) zatím není zachycena (chtělo by to `0xBF`/`0x08`).
- Varianta B2 — facet/mapa je natvrdo Felucca (viz sekce výše), pro shard s víc mapami by
  potřebovala vlastní offset nalezený stejnou živou seancí.
- Místnosti (rooms) — admin heslo se zadává jako obyčejný argument příkazové řádky (vidí ho
  kdokoliv s přístupem na VPS).
- Multiplayer — po pádu appky (bez řádného odpojení) je stejné jméno v místnosti blokované až 15 s.
- Příkazy ve hře — zatím jen `-c`, `-panic`, `-unpanic`. `-find`, `-mark`, `-unmark`, `-share`,
  `-who` ze staré UOAM chybí. Nový příkaz se v klientu projeví až po jeho restartu.
