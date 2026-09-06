# Revit Set Tags (Revit 2027)

Videoyla tersine mühendislik yapılmış eklenti: seçilen tag'leri, tıkladığınız bir başlangıç
noktasından itibaren eşit aralıklı, düzenli bir kolon halinde dizer. Kaynak: *"Revit API (C#) -
Tags ordering"* (ProEngineering). Video analizi Gemini **agentic video understanding** ile
yapıldı (`gemini-3.7-flash`, native `processing_call`/`processing_result` kayıtlarıyla doğrulandı).

## Özellikler (videodaki davranışla birebir)

- **Get tags** — seçim moduna geçer; yalnızca `IndependentTag` öğeleri seçilebilir (pencere/box
  seçim destekli). Ardından kolonun **başlangıç noktasını** tıklarsınız.
- Tag'ler, işaret ettikleri ("host") elemanların konumuna göre sıralanır ve
  `Tag(i) = Origin − i × Spacing × Up(view)` formülüyle dizilir: ilk tag tıklanan noktaya,
  sonrakiler altına, eşit **Spacing (m)** aralıkla. Sıralama leader çizgilerinin kesişmesini
  azaltacak şekilde yapılır (videodaki sonuç).
- Leader'lar gerekirse açılır; tag'ler taşınsa da host elemanlara bağlı kalır.
- **Post-correction**: görünümde tag seçip **▲ / ▼** ile **Shift (m)** kadar yukarı/aşağı
  kaydırma (videodaki `ø28` tag'i örneği; Shift değeri anlık değiştirilebilir).
- 3B, plan ve kesit görünümlerinde çalışır (video: *"Work with 3-D, plans and sections views"*).
- Pencere başlığı ve düzeni videodaki paletle aynıdır: **ProEngineering Bim** — `Get tags`,
  `Tags count`, `Spacing (m)`, `Shift (m)` + ▲/▼.
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
> 2027.2.0) bu TFM'i gerektirir; `IndependentTag.TaggedLocalElementId` gibi eski üyeler 2027'de
> kaldırıldığı için kod `GetTaggedLocalElements()` kullanır. Farklı Revit sürümü için
> `RevitSetTags.csproj` içindeki paket sürümlerini ve hedef çerçeveyi güncelleyin.

## Kullanım

1. Şeritte **Pro Tools → Set Tags**'e tıklayın; palet açılır.
2. **Get tags** → tag'leri seçin → **Finish** → kolonun başlangıç noktasını tıklayın.
3. İnce ayar için görünümde tag seçin, **Shift (m)** girin, **▲/▼**'ya basın.

## Varsayımlar ve sınırlar

- Video 55 saniyelik ekran kaydıdır; kaynak kod gösterilmez, sesli anlatım videonun konusuyla
  ilgisizdir (stok aritmetik dersi). Sıralama formülü videodaki sonuçtan çıkarılmıştır.
- **PickPoint** 3B görünümde aktif çalışma düzlemi ister; düzlem yoksa eklenti uyarı verir.
  Plan/kesit görünümlerinde sınırlama yoktur.
- `HasLeader` desteklenmeyen tag türlerinde leader adımı sessizce atlanır (tag yine taşınır).

## Proje yapısı

| Dosya | Görev |
| --- | --- |
| `App.cs` | Şerit sekmesi/paneli ve düğme kaydı |
| `Commands/ShowSetTagsCommand.cs` | Modeless paleti açan komut |
| `UI/SetTagsWindow.cs` | Videodaki paletin kopyası (Get tags / Spacing / Shift ▲▼) |
| `Handlers/SetTagsHandler.cs` | API bağlamında çalışan `IExternalEventHandler` + seçim filtresi |
| `Services/TagOrderingService.cs` | Kolon yerleştirme, sıralama ve kaydırma çekirdeği |
| `RevitSetTags.addin` | Revit eklenti manifest'i |
