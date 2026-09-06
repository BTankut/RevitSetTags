# Revit Set Tags (Revit 2027)

Videoyla tersine mühendislik yapılmış eklenti: seçilen tag'leri, belirlediğiniz bir başlangıç
noktasından itibaren eşit aralıklı bir kolon halinde dizer (demo: *"Revit API (C#) - Tags ordering"*,
ProEngineering kanalı).

## Özellikler (videodaki davranışla birebir)

- **Pick elements** — Revit seçim moduna geçer; yalnızca `IndependentTag` öğeleri seçilebilir.
- **Set tags** — ilk tag'in başlık noktasını **Start (X, Y, Z)**'e koyar, sonraki her tag'i bir
  **Shift (X, Y, Z)** vektörü ileriye taşır. Sonuç: eşit aralıklı, düzenli bir tag kolonu.
- Tag'ler **Shift yönüne göre** sıralanır (saf dikey kaydırmada alttan üste) — videodaki
  "chosen tags are transferred and ordered" davranışı.
- Leader'lar açılır (gerekirse), böylece tag'ler taşınsa da işaret ettikleri elemanlara bağlı kalır.
- **Reset** — aynı Revit oturumunda ilk konumlara geri döndürür.
- 3B, plan ve kesit görünümlerinde çalışır (tag'ler görünüm bazlı olduğu için seçtiğiniz tag'lerin
  görünümü önemlidir).
- Değerler **metre** girilir, Revit içi birimine çevrilir.

## Kurulum

1. Derleyin:
   ```
   dotnet build -c Release
   ```
2. Çıktıyı Revit 2027 eklenti klasörüne kopyalayın (Windows):
   ```
   %AppData%\Autodesk\Revit\Addins\2027\
   ```
   Kopyalanacaklar: `RevitSetTags.dll` ve `RevitSetTags.addin` (addin dosyasında `Assembly`
   zaten `RevitSetTags.dll` olarak ayarlı; DLL ile aynı klasörde durmalıdır).
3. Revit 2027'yi başlatın → **Pro Tools** sekmesi → **Tag Tools** paneli → **Set Tags**.

> Not: Derleme .NET 10 hedefler (`net10.0-windows`) — Revit 2027 API paketleri (Nice3point
> 2027.2.0) bu TFM'i gerektirir. Farklı Revit sürümü için `RevitSetTags.csproj` içindeki
> paket sürümlerini ve hedef çerçeveyi ilgili sürüme göre değiştirin.

## Kullanım

1. Şeritte **Pro Tools → Set Tags**'e tıklayın; pencere açılır.
2. **Pick elements** → tag'leri seçin → **Finish** (Esc = iptal).
3. Start ve Shift değerlerini girin (metre; varsayılanlar videodaki gibidir:
   Start `1.0, 1.0, 0.0`, Shift `0.0, 0.0, 0.1`).
4. **Set tags** → tag'ler sıralanır ve dizilir. **Reset** → orijinal konumlar.

## Varsayımlar ve sınırlar

- Video 55 saniyelik bir ekran kaydıdır; sesli anlatım yoktur (yalnızca müzik), kaynak kod
  gösterilmez. Sıralama mantığı videodaki sonuçtan (dikey kolon + eşit kaydırma) çıkarılmıştır:
  tag'ler Shift vektörünün yönüne göre izdüşümle sıralanır.
- **Reset** yalnızca oturum içindedir (konumlar bellekte tutulur); Revit kapanınca kaybolur.
- Aynı anda birden fazla görünümün tag'leri seçilirse hepsi aynı kolona dizilir.
- `HasLeader` desteklenmeyen tag türlerinde leader adımı sessizce atlanır (tag yine taşınır).

## Proje yapısı

| Dosya | Görev |
| --- | --- |
| `App.cs` | Şerit sekmesi/paneli ve düğme kaydı |
| `Commands/ShowSetTagsCommand.cs` | Modeless pencereyi açan komut |
| `UI/SetTagsWindow.cs` | Videodaki diyaloğun birebir kopyası (Pick / Set / Reset) |
| `Handlers/SetTagsHandler.cs` | API bağlamında çalışan `IExternalEventHandler` + seçim filtresi |
| `Services/TagOrderingService.cs` | Sıralama ve taşıma çekirdeği, orijinal konum deposu |
| `RevitSetTags.addin` | Revit eklenti manifest'i |
