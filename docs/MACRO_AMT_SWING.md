# MacroAmtSwingBot — swing trading: bias makro + COT + volume profile

cBot untuk cTrader Automate (`src/MacroAmtSwingBot/MacroAmtSwingBot.cs`, **satu file**).
Default simbol XAUUSD, tetapi bisa dipakai untuk pasangan forex dan indeks lewat preset.

> **Tidak ada janji profit.** Bot ini adalah alat eksekusi disiplin: ia memaksa aturan
> (arah makro, positioning, lokasi value area, risiko) dan mencatat setiap penolakan.
> Backtest yang bagus bukan bukti hasil masa depan. Baca `docs/FORWARD_TEST.md` sebelum
> memakai uang sungguhan.

| Item | Status |
|---|---|
| Compile | ya — `dotnet build src/MacroAmtSwingBot/MacroAmtSwingBot.csproj` (CI: `.github/workflows/macroamt.yml`) |
| Unit test pure core | ya — `dotnet test tests/MacroAmt.Tests/MacroAmt.Tests.csproj` |
| Backtest yang disertakan | tidak ada (data tick broker tidak bisa disertakan di repo) |
| Demo/forward test | belum dijalankan |

---

## 1. Arsitektur

Satu file, dua lapisan:

```
MacroAmtSwingBot.cs
├── PURE CORE (tanpa cAlgo.API)                        diuji di tests/MacroAmt.Tests
│   ├── ProfileBar / ProfileHistogram / VolumeProfile   distribusi volume, merge, value area 70%
│   ├── ValueProfileEngine                              cache histogram harian, session + composite + migrasi
│   ├── MigrationTracker / RegimeClassifier             Up/Down/Overlap + ADX -> trend/range
│   ├── CotCsv / CotSnapshot / CotPolicy                CSV COT, lag rilis, filter Soft/Hard
│   ├── BiasPolicy                                      expiry, conviction, skor proxy EMA50/200
│   ├── PresetPolicy                                    aturan per pasar (gold/forex/index)
│   ├── AuctionSignals                                  model 1, 2, 3 (fungsi murni per bar close)
│   ├── SignalGate                                      satu sinyal per value area per arah
│   ├── Sizing                                          risk %, volume, correlation groups
│   └── TimeGuards                                      filter Jumat
└── ROBOT ([Robot] : cAlgo.Robot)                      #if !MACROAMT_CORE_ONLY
    ├── data & warmup          D1 / entry TF / profile TF + ATR(D1) + ADX(D1)
    ├── profil                 rebuild hanya saat D1 baru; bin = ATR(D1)×0.05 atau fixed
    ├── entry                  cek filter global -> eval model berurutan -> submit
    ├── manajemen              TP1 parsial, breakeven, trailing ATR, time stop, scale-in
    ├── risiko                 daily loss, drawdown, max posisi, grup korelasi, netting
    └── visual & log           POC/VAH/VAL, marker, info panel, CSV trade log
```

Unit test meng-compile **file yang sama** dengan `MACROAMT_CORE_ONLY`, jadi kode yang diuji
identik dengan kode yang jalan di bot (tidak ada salinan logika).

### Alur satu bar close

```
tick / timer
  └─ ada bar entry TF baru yang close?
       ├─ tidak -> hanya manajemen posisi (TP1/BE/trail/time stop)
       └─ ya
           1. pastikan profil untuk D1 terakhir sudah dibangun (cache)
           2. update gate (close melewati tepi VA lawan -> buka blokir)
           3. scale-in (jika aktif, hanya di sepertiga VA yang benar)
           4. filter global: bias expired, Jumat, daily loss, drawdown, spread, max posisi
           5. untuk tiap model (1,2,3) × arah:
                preset -> COT -> geometri sinyal -> RR (harga live) -> ukuran posisi -> order
           6. maksimum satu entry per bar close
```

Tidak ada pembacaan bar yang masih forming. `Bars.Last(1)`/indeks `Count-2` dipakai di semua
perhitungan; sinyal basi (bar sudah lewat lebih dari satu TF) tidak dieksekusi.

---

## 2. Logika inti

### Volume profile
- Sumber: `ProfileTimeframe` (default M30) tick volume.
- Volume tiap bar dibagi rata ke bin antara Low–High.
- Bin: `Auto = ATR(D1,14) × 0.05` (default) atau `Fixed`.
- `SessionProfile` = hari terakhir yang selesai; `CompositeProfile` = `CompositeDays` hari
  terakhir (default 20). Di-cache per hari; rebuild hanya saat D1 baru dibuka.
- Value Area 70%: mulai dari bin POC, tambah bin berikutnya yang lebih besar (satu bin atas
  vs satu bin bawah) sampai target tercapai. VAH/VAL = tepi luar bin terpilih.

### Migrasi & regime
- Bandingkan composite sekarang vs composite satu hari sebelumnya: **Up** bila VAL dan POC
  sama-sama naik; **Down** bila sama-sama turun; selain itu **Overlap**.
- **Trend** bila migrasi searah ≥ `TrendMigrationMin` (2) **dan** ADX(D1,14) > 20.
- **Range** bila overlap ≥ `RangeOverlapMin` (3) **dan** ADX < 20.
- Selain itu `Unknown` → tidak ada entry (kecuali override).

### Tiga model entry (semua di bar close)
1. **Failed auction** — excursion ≥ `ExcursionMinAtr` di luar VAL/VAH dalam N bar, lalu close
   kembali ke dalam VA. SL di ekstrem excursion ± 0.2 ATR. TP1 = POC, TP2 = tepi VA lawan.
   Opsional limit di VAL/VAH untuk retest.
2. **Breakout & retest** — minimal 2 close berurutan di luar VAH/VAL + buffer, lalu pullback ke
   zona VAH ± 0.25 ATR dan bar rejection yang close kembali di luar. SL di bawah retest low −
   0.3 ATR. TP1 = VAH + tinggi VA, TP2 = 2× tinggi VA (atau trailing ATR setelah TP1).
3. **Mean reversion** — hanya saat regime Range: fade excursion di luar VA, close kembali ke
   dalam, target POC lalu tepi VA lawan.

### COT tanpa look-ahead
Data Selasa dipublikasikan Jumat 15:30 ET. Bot memakai 21:00 UTC sebagai waktu rilis
konservatif; record baru hanya dipakai setelah waktu itu. Perubahan 4 minggu dihitung dari 4
observasi mingguan sebelumnya (bukan tanggal kalender). Data lebih tua dari `COT max age`
(10 hari) mengikuti `COT basi` = Ignore/Block.

### Satu sinyal per value area
Setelah entry, arah itu diblokir sampai profil composite baru terbentuk (VAL/VAH/hari berubah)
atau harga close melewati tepi VA lawan.

---

## 3. Tabel parameter

Kolom **Opt** = prioritas optimasi: **P1** optimasi dulu (paling berpengaruh), **P2** setelah
itu, **P3** biarkan tetap (risiko/eksekusi).

### 3.1 Preset

| Parameter | Default | Range | Opt | Catatan |
|---|---|---|---|---|
| Market preset | Gold | Gold / Forex / Index / VixExperimental | P3 | menentukan model yang boleh jalan |
| Entry timeframe | H4 | H4 / D1 | P2 | sinyal hanya di bar close TF ini |
| Expected symbol | (kosong) | teks | P3 | kosong = simbol apa pun |
| Index min pullback % (20D high) | 2.0 | 0.1–20 | P1 | hanya preset Index |
| VIX symbol | (kosong) | teks | P3 | kalau diisi, preset Index & VIX wajib VIX ≥ minimum; kosong = diabaikan |
| VIX minimum | 20 | 0–100 | P2 | ambang VIX |
| Allow experimental VIX preset | false | bool | P3 | harus true untuk preset VIX |

### 3.2 Bias

| Parameter | Default | Range | Opt | Catatan |
|---|---|---|---|---|
| Bias source | Manual | Manual / AutoProxy | P3 | AutoProxy = tren D1 simbol proxy |
| Bias direction | Neutral | Long / Short / Neutral | **P3 (keputusan trader)** | Neutral = hanya model 3 di forex/gold |
| Bias conviction | Medium | Low(1)/Medium(2)/High(3) | P3 | 0.5x / 1x / 1.5x risiko, tetap di-cap |
| Bias expiry date | (kosong) | yyyy-MM-dd | P3 | lewat tanggal = tidak ada entry baru |
| Proxy symbol | USDX | teks | P3 | harus ada di broker |
| Proxy invert | true | bool | P3 | USDX vs XAUUSD = true |
| Proxy EMA cepat / lambat / slope | 50 / 200 / 5 | 5–200 / 20–400 / 1–60 | P2 | skor proxy saja |

### 3.3 COT

| Parameter | Default | Range | Opt |
|---|---|---|---|
| COT mode | Off | Off / Soft / Hard | P2 |
| COT CSV path | (kosong) | path absolut | P3 |
| COT market filter | GOLD | teks | P3 |
| COT max age (hari) | 10 | 3–60 | P3 |
| COT basi | Ignore | Ignore / Block | P3 |

### 3.4 Profile

| Parameter | Default | Range | Opt |
|---|---|---|---|
| Profile timeframe | M30 | M30 / H1 / H4 | P2 |
| Bin size mode | Auto | Auto / Fixed | P2 |
| Bin size fixed | 1.0 | > 0 | P3 |
| Auto bin = ATR(D1) × | 0.05 | 0.005–0.5 | P2 |
| Value area % | 70 | 50–95 | **P1** |
| Composite days | 20 | 3–120 | **P1** |
| Max profile bins | 4000 | 100–20000 | P3 |
| Warmup days | 6 | 0–60 | P3 |

### 3.5 Regime

| Parameter | Default | Range | Opt |
|---|---|---|---|
| Regime override | Auto | Auto / ForceTrend / ForceRange | P3 |
| Trend: migrasi minimum | 2 | 1–10 | **P1** |
| Range: overlap minimum | 3 | 1–20 | **P1** |
| ADX trend threshold | 20 | 5–60 | **P1** |
| ADX period | 14 | 5–60 | P3 |
| ATR period | 14 | 5–60 | P3 |

### 3.6 Models

| Parameter | Default | Range | Opt |
|---|---|---|---|
| Model 1 / 2 / 3 | true / true / true | bool | P2 |
| Excursion lookback (bar) | 5 | 1–50 | P2 |
| Excursion minimum (ATR) | 0.25 | 0.05–3.0 | **P1** |
| Close-inside buffer (ATR) | 0.25 | 0–2.0 | P2 |
| SL buffer model 1 & 3 (ATR) | 0.20 | 0–2.0 | P2 |
| Breakout buffer (ATR) | 0.10 | 0–1.0 | P2 |
| Retest zone (ATR) | 0.25 | 0.05–2.0 | P2 |
| SL buffer model 2 (ATR) | 0.30 | 0–2.0 | P2 |
| Retest window (bar) | 20 | 3–200 | P2 |
| Min reward:risk (TP2) | 1.5 | 0.5–10 | **P1** |
| Entry model 1 pakai limit retest | false | bool | P2 |
| Limit expiry (bar) | 3 | 1–50 | P3 |
| Model 2 trailing setelah TP1 | false | bool | P2 |

### 3.7 Risk & management

| Parameter | Default | Range | Opt |
|---|---|---|---|
| Risk per trade % | 0.5 | 0.01–20 | P3 |
| Max risk per trade % (cap) | 2.0 | 0.05–50 | P3 |
| Max total open risk % | 3.0 | 0.1–100 | P3 |
| Daily loss limit % | 2.0 | 0.1–50 | P3 |
| Max equity drawdown % (halt) | 10 | 1–90 | P3 |
| Max open positions | 3 | 1–20 | P3 |
| Max spread (× ATR D1) | 0.02 | 0.001–0.5 | P3 |
| Correlation groups | (kosong) | `EURCHF,USDCHF;AUDCAD,AUDUSD` | P3 |
| Max risk per grup % | 0 | 0 = 1 posisi/grup | P3 |
| Scale-in maksimum | 0 | 0–3 | P2 |
| TP1 close % | 50 | 0–90 | **P1** |
| Trailing mode | None | None / AtrTrail | P2 |
| Trail ATR multiple | 0.5 | 0.1–5 | P2 |
| Breakeven setelah TP1 | true | bool | P3 |
| Max bar tanpa TP1 | 60 | 0 = mati | **P1** |
| Jumat: stop entry setelah jam UTC | 20 | -1 = mati | P3 |
| Max swap negatif per lot/hari | 0 | 0 = mati | P3 |
| Dry run | false | bool | P3 |
| Label prefix | MAS | teks | P3 |

### 3.8 Visual, log & eksekusi

| Parameter | Default | Range | Opt |
|---|---|---|---|
| Gambar garis profil | true | bool | P3 |
| Tampilkan info panel | true | bool | P3 |
| Tandai sinyal di chart | true | bool | P3 |
| Log level | Normal | Signals / Normal / Verbose | P3 |
| CSV trade log | false | bool | P3 |
| CSV log directory | (kosong) | path | P3 |
| Dry run (tidak kirim order) | false | bool | P3 |
| Label prefix | MAS | teks | P3 |

Arti log level: *Signals* = hanya trade & manajemen; *Normal* = tambah penolakan yang sempat
membentuk setup (mis. "RR 1.1 < 1.5"); *Verbose* = setiap alasan penolakan, termasuk preset dan
COT. Untuk backtest panjang, pakai *Normal*.

Label order berbentuk `<prefix>-<SYM>-M<model>-<seq>-<HHmm>` (mis. `MAS-XAU-M1-007-1030`) sehingga satu
simbol + satu model mudah dilacak di tab History, dan bot bisa mengadopsi posisinya sendiri
setelah restart.

---

## 4. Panduan backtest

### Data
1. cTrader → **Backtesting** → pilih **Tick data from server** (bukan M1); minta 5+ tahun.
   Tick data memberi spread intra-bar yang lebih realistis untuk SL/TP.
2. Simbol: **XAUUSD** + minimal **2 pasangan forex** (mis. EURUSD, GBPUSD atau USDJPY), dan
   satu indeks kalau broker menyediakan (mis. US500/US30) untuk preset Index.
3. Set komisi dan swap sesuai broker. Bot tidak menebak biaya.
4. Backtest 2019–2025 mencakup easing, inflasi/kenaikan suku bunga, dan resesi risiko — tiga
   rezim makro berbeda. Jangan optimasi hanya pada satu rezim.

### Walk-forward
- Bagi: **in-sample 2 tahun → out-of-sample 6 bulan**, geser 6 bulan, ulangi.
- Optimasi hanya parameter **P1** (maks 3–4 parameter sekaligus, grid kecil). Parameter P3
  jangan digeser-geser; itu batas risiko.
- Simpan hasil per fold; yang dicari adalah **stabilitas**, bukan satu kurva terbaik.
- Minimal **100 trade** per konfigurasi untuk klaim apa pun. Dengan H4 dan satu sinyal per VA,
  XAUUSD bisa hanya menghasilkan 15–40 trade/tahun — perhitungkan error sampling.

### Metrik yang harus dibaca
| Metrik | Target kasar | Kenapa |
|---|---|---|
| Profit factor setelah biaya | > 1.2 | di bawah itu, biaya/slippage menghapus edge |
| Expectancy (R) | > 0.15R | rata-rata hasil per trade |
| Max drawdown | < 10–15% ekuitas | harus cocok dengan `MaxEquityDrawdownPercent` |
| Jumlah trade & win rate | 35–50% dengan RR 1.5–2 | model TP1/TP2 parsial memang win rate sedang |
| Win rate per model | dipisah M1/M2/M3 | satu model bisa menutupi model lain yang rugi |
| MAE/MFE | dicatat manual | mengukur apakah SL terlalu lebar/sempit |
| Distribusi R | ekor kiri terbatas | memastikan tidak ada trade yang lolos batas risiko |

### Uji wajib (sanity)
- Bias Neutral: model 1/2 harus **nol** trade (hanya model 3 di forex jika range).
- `BiasExpiryDate` di masa lalu: **nol** entry baru + log "Bias expired".
- COT Hard dengan data yang tidak setuju: jumlah trade turun ke arah yang benar.
- Scale-in 0 vs 3: total risiko tidak boleh melewati `MaxTotalOpenRiskPercent`.
- Bandingkan spread ATR filter 0 vs 0.02: kalau hasil berubah drastis, strategi terlalu
  bergantung pada spread.

---

## 5. Batasan yang diketahui

1. **Tick volume ≠ volume nyata.** Untuk forex/indeks CFD, "volume" adalah jumlah tick broker.
   Distribusi volume profile karena itu proxy. Di XAUUSD kualitasnya lebih baik, tetapi tetap
   bukan volume bursa.
2. **Bias makro manual.** Tidak ada data fundamental di cTrader. Disiplin expiry + review
   berkala adalah bagian dari strategi, bukan opsional. Mode AutoProxy hanya proksi tren
   EMA50/200, bukan analisis makro.
3. **COT dari CSV lokal.** Tidak ada unduhan otomatis. Format kolom harus
   `date,market,noncomm_long,noncomm_short`; lag publikasi dimodelkan +3 hari 21:00 UTC.
   Nama market berbeda antar broker/sumber (mis. "GOLD - COMMODITY EXCHANGE INC.").
4. **Value area berbasis bin.** VAH/VAL adalah tepi bin, bukan level "benar" di pasar; hasil
   bergantung pada ukuran bin.
5. **Komposit rolling.** Migrasi membandingkan dua jendela N hari yang bergeser satu hari
   (bukan dua periode yang benar-benar terpisah). Ini pilihan sadar agar responsif.
6. **Eksekusi parsial.** TP1 ditutup dengan market order saat tersentuh; harga bisa berbeda
   dari level TP1, terutama saat news. Breakeven juga modify, bukan jaminan.
7. **Netting account.** Scale-in dimatikan kecuali `Izinkan scale-in di akun netting`. Arah
   berlawanan diblokir.
8. **Gap & swap.** Gap akhir pekan bisa melewati SL. Swap negatif besar diblokir hanya jika
   filternya diaktifkan.
9. **VIX jarang tersedia.** Preset VIX mati secara default; tanpa simbol VIX, kondisi VIX
   diabaikan.
10. **Tidak ada state persisten.** Setelah restart, posisi diadopsi dari label dan SL/TP-nya;
    TP1/BE/trailing bisa direkonstruksi sebagian. Untuk akun live, restart saat ada posisi
    perlu dicek manual.
11. **Backtest bukan bukti.** cTrader memakai data tick brokernya; kualitas dan spread historis
    berbeda dari live. Angka backtest tidak boleh dijadikan janji.

---

## 6. Deployment

1. **Demo dulu, minimal 4–8 minggu.** Parameter sama dengan backtest. Bandingkan log bot
   dengan chart.
2. **Mulai kecil:** `RiskPerTradePercent = 0.25`, `MaxRiskPerTradePercent = 1.0`,
   `MaxTotalOpenRiskPercent = 2.0`. Naikkan hanya setelah forward test bersih.
3. **Atur bias dengan jadwal.** Isi `BiasDirection`, `BiasConviction`, dan `BiasExpiryDate`
   setiap minggu (mis. Senin pagi). Kalau expiry lewat, bot berhenti entry — itu fitur, bukan bug.
4. **Cek penolakan setiap hari.** Alasan yang sering muncul memberi tahu parameter mana yang
   perlu diubah:
   - `volume minimum broker butuh risiko ...` → akun terlalu kecil untuk jarak SL; turunkan
     `MinRr`/perkecil SL, atau pakai simbol dengan nilai tick lebih kecil.
   - `spread ... > ...` → jam likuiditas buruk; jangan trading di rollover.
   - `RR live ... <` → harga bergerak setelah close; ini normal.
5. **Satu instance per simbol**, `LabelPrefix` berbeda, dan isi `CorrelationGroups` agar
   eksposur mata uang yang sama tidak menumpuk.
6. **Perbarui COT mingguan** (Jumat malam) dan simpan file di path yang stabil.
7. **Berhenti dan evaluasi** kalau drawdown halt tercapai atau 20 trade terakhir di bawah
   ekspektasi; jangan menurunkan batas risiko untuk "mengejar".
8. **Jangan menambah lot manual** saat posisi floating negatif. Bot tidak memakai martingale;
   scale-in hanya jika diaktifkan dan hanya di sepertiga VA yang benar.

---

## 7. Format CSV COT

Contoh siap pakai: **`config/cot_sample.csv`** (ganti angka dengan data CFTC terbaru).

```csv
date,market,noncomm_long,noncomm_short
2026-10-20,GOLD - COMMODITY EXCHANGE INC.,245000,118000
2026-10-13,GOLD - COMMODITY EXCHANGE INC.,238500,121000
2026-10-06,GOLD - COMMODITY EXCHANGE INC.,231000,124500
2026-09-29,GOLD - COMMODITY EXCHANGE INC.,228000,130000
2026-09-22,GOLD - COMMODITY EXCHANGE INC.,220000,133000
```

- `date` = tanggal laporan CFTC (Selasa), format `yyyy-MM-dd`.
- `market` = teks nama pasar; dicocokkan sebagai substring case-insensitive dengan
  `COT market filter`.
- Angka boleh mengandung pemisah ribuan; pemisah kolom `,` atau `;`.
- Baris yang tidak bisa di-parse dilewati dan dihitung di log.
- File dibaca ulang saat `LastWriteTime` berubah (maksimal sekali per jam).

---

## 8. Troubleshooting cepat

| Gejala | Cek |
|---|---|
| Tidak ada trade sama sekali | log harus menyebut alasannya. Bias neutral? Regime Unknown (ADX/streak)? Profil belum cukup hari? |
| "composite profile belum siap" | butuh minimal `CompositeDays` hari data M30 sebelum hari terakhir + `WarmupDays`; tambah history |
| Semua sinyal ditolak RR | `MinRr` terlalu tinggi atau `SlBufferAtr` terlalu lebar relatif terhadap VA |
| Bot monitor-only saat start | parameter invalid (lihat log) atau simbol tidak cocok dengan `Expected symbol` |
| COT tidak terbaca | path absolut, akses FullAccess, nama market, format tanggal |
| Posisi tidak dikelola setelah restart | label harus berawalan `LabelPrefix + "-"`; bot mengadopsi posisi saat tick berikutnya |
