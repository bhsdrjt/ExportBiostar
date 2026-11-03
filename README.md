# BioStar Exporter — source review build 0.1

C# / WinForms / .NET 10. Aplikasi membaca SQL Server, memfilter user, memvalidasi fingerprint/CSN, dan menulis ZIP lokal.

## Status dan batas penting

**Ini build untuk review sumber/pengujian, bukan implementasi migrasi produksi yang sudah disetujui.** DDL dan semantik internal BioStar belum diberikan lengkap. Aplikasi tidak mengklaim kompatibilitas vendor dan tidak menyinkronkan perangkat.

Semua ZIP menggunakan:
- `schema_contract_id = biostar-source-review-v3`.
- `users.csv` menyertakan `IDXUSRID` dan `IDXUSRIDNUM` sesuai sumber, termasuk NULL. Hanya `USRIDORD` yang dihilangkan; kolom profil lain tetap diekspor. Metadata inspeksi schema boleh tetap mencantumkan definisi `USRIDORD`, bukan nilainya.
- Importer harus mengenali header v3; jangan mengganti ID kontrak manual. Hilangkan `USRIDORD` dari INSERT setelah memastikan nullability/default dan trigger tujuan aktif. Kedua kolom IDX dipetakan sesuai tipe DDL, mempertahankan NULL. Jika USRID berubah dan IDX non-NULL ditemukan, blokir untuk review aturan metadata. ZIP v1/v2 lama tidak diubah.
- `production_import_approved = false`.
- Metadata sumber di `export_context`, termasuk schema kolom dan keterbatasan.

Importer produksi HARUS menolak kontrak review ini. Jangan mengubah flag manual. Setelah DDL, metadata, dependensi, zona waktu, dan uji perangkat selesai, buat kontrak produksi eksplisit dan perbarui exporter/importer bersama.

Profil ekspor memakai allowlist kolom, bukan SELECT *. Tidak mengekspor PIN/login/password, face, foto, key, atau hubungan access group. Referensi USRGRUID/PERMUID dan metadata internal dipertahankan sebagai data review, bukan instruksi import. Metadata belum dipetakan atau dibentuk ulang. Keamanan/eligibility penuh belum terbukti.

## Fitur tersedia

- Windows atau SQL Authentication; TLS dan validasi sertifikat wajib, tanpa opsi bypass.
- Pencarian USRID/nama literal, Department (Semua/Tanpa/nilai tertentu), rentang CREATEDT inklusif per hari.
- 100 user per halaman; pilihan tetap antarhalaman, semua hasil filter, reset pilihan saat filter berubah.
- Jumlah fingerprint/CSN dihitung terpisah.
- Pemeriksaan ulang pilihan dan data dalam transaksi SNAPSHOT.
- Blokir non-CSN, blacklist, kartu yatim/ambigu, duplikasi CSN/USRID/index fingerprint, template kosong/besar.
- ZIP empat CSV + manifest, Base64, NULL marker, SHA-256, verifikasi ulang, pembatalan dan cleanup.
- ACL ZIP privat untuk user Windows pembuat dan LocalSystem; file tidak dioverwrite.
- Simpan metadata kolom ke JSON untuk pemeriksaan. Bukan DDL lengkap; tidak mencakup index/default/FK/trigger.

## Persyaratan SQL

- SQL Server dengan database compatibility level minimal 130 (OPENJSON).
- Akun dengan SELECT pada tabel sumber dan visibilitas metadata yang diperlukan. Aplikasi tidak menambahkan izin.
- SNAPSHOT isolation sudah diaktifkan DBA atau gunakan salinan database yang dikonfigurasi tepat. Aplikasi tidak mengubah pengaturan DB; ekspor gagal jika snapshot tidak tersedia.
- Kolom wajib dan tipe dicek di `BioStarSource.cs`; jika schema berbeda, jangan mengubah DB agar cocok. Tinjau DDL dan sesuaikan kontrak.
- ID relasi menggunakan int untuk build ini.
- Eligibility review: DEL=N, ISUSRGR=N, LGNID/PIN NULL atau kosong. Ini asumsi eksplisit yang perlu diverifikasi sebelum produksi; akun berizin administratif tanpa login belum dapat diklasifikasikan lengkap.
- T_BLKL wajib tersedia agar pemeriksaan blacklist tidak dilewati.
- Tipe CSN 0 dengan NM=CSN, TYP=1, MD=C, CRDLAYUID=NULL.
- Dua template fingerprint wajib terisi dalam build review; schema yang mengizinkan satu template saja belum didukung.

Gunakan nilai server/domain yang cocok dengan sertifikat. SQL error ditampilkan sebagai nomor tanpa pesan server mentah karena pesan SQL dapat berisi credential. Error 3951/3952 yang berkaitan SNAPSHOT perlu ditangani DBA, bukan mematikan validasi konsistensi.

## Menjalankan

Build yang dipublish berada di `publish\BioStarExporter.exe`. Jalankan tanpa argumen untuk UI.

Dari source dengan SDK .NET 10:

```powershell
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

Self-check tanpa koneksi database:

```powershell
dotnet bin\Release\net10.0-windows\BioStarExporter.dll --self-test
```

Publish self-contained:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

Salin SELURUH folder publish, bukan EXE saja. Dependency native SQL client harus ikut. Publish tidak memakai trimming.

## Alur operator

1. Gunakan salinan database dan akun read-only. Siapkan media output terenkripsi.
2. Isi server/instance, database, dan autentikasi lalu Tes koneksi / Muat user.
3. Terapkan filter. CREATEDT mengikuti nilai database tanpa konversi zona waktu; tahun 1900 tidak diperbaiki.
4. Pilih user per halaman atau semua hasil filter.
5. Klik Validasi pilihan. Pilihan dicek ulang dalam snapshot, tanpa membuat ZIP.
6. Klik Export ZIP uji, setujui peringatan, pilih file baru.
7. Tunggu verifikasi ZIP selesai. Paket belum boleh diimpor ke produksi.
8. Jika perlu bagikan schema kolom, gunakan Simpan metadata schema. Lengkapi DDL index/constraint/default/trigger secara terpisah.

Department khusus dibedakan dari nilai asli berdasarkan indeks internal, bukan teks label. Pilihan manual menjadi mode individual; pilihan semua hasil filter dibandingkan sebagai himpunan saat ekspor. Data credential berasal dari snapshot ekspor terbaru, bukan preview lama.

## Format paket review

```text
manifest.json
users.csv
fingerprints.csv
cards.csv
user_cards.csv
```

UTF-8 tanpa BOM, CRLF, semua field quoted. NULL ditulis `\N`; backslash non-NULL digandakan sebelum escaping quote. Biner Base64. DateTime tujuh angka pecahan tanpa Z karena UTC tidak terbukti. Tipe DateTimeOffset mempertahankan offset.

Manifest `files`: `name`, `size_bytes`, `record_count`, `sha256`. Ukuran/hash dihitung dari byte CSV sebelum kompresi. `totals` berupa count per nama CSV tanpa ekstensi. `export_context` menyimpan sumber, filter tanpa teks pencarian, schema dan keterbatasan. Format review berbeda dari kontrak produksi yang belum disetujui.

Batas: 100.000 user hasil filter; 100.000 record per CSV; template biner maksimal 1 MiB per field; field encoded maksimal 2 MiB; CSV kumulatif maksimal 1 GiB; manifest maksimal 4 MiB. Data tidak dipotong bila melewati batas. Daftar user dibaca ke memori secara terbatas lalu dipaginasi di UI; SQL/ZIP diproses bertahap. Untuk volume lebih besar, ganti daftar menjadi server-side paging setelah benchmark.

Checksum bukan tanda tangan. ZIP bukan enkripsi. Jangan buka/simpan ulang CSV melalui Excel karena perubahan tipe, nol depan, tanggal, dan risiko formula. Jangan masukkan paket ke Git atau unggah ke layanan publik. ACL tidak melindungi dari administrator mesin; gunakan enkripsi volume dan prosedur transfer aman. Rename mempertahankan ACL privat file; sesuaikan akses secara sadar saat memindahkan ke akun importer.

## Pengujian

Self-check mencakup CSV multiline/Unicode/quotes/backslash/NULL/kosong, Base64 byte-for-byte, tanggal presisi, batas rentang filter, lima entry, checksum, tamper, file-existing refusal, dan cancellation. Database BioStar nyata belum diuji; tidak ada koneksi sumber diberikan.

Sebelum produksi: verifikasi seluruh DDL dan metadata keamanan, mapping grup/access group, eligibility akun, format tanggal, kontrak NestJS, konflik/suffix USRID, dan autentikasi fingerprint/kartu pada perangkat uji. Jangan menghapus gate review untuk melewati pekerjaan tersebut.
