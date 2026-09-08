# Revit Set Tags (Revit 2027)

Videodan tersine mühendislikle yazılmış eklenti: seçilen tag'leri, tıkladığınız bir başlangıç
noktasından itibaren eşit aralıklı, düzenli bir kolon halinde dizer ve her leader'a elemanlara
doğru uzanan eşit uzunlukta bir omuz (dirsek) verir. Kaynak: *"Revit API (C#) - Tags ordering"*
(ProEngineering, https://www.youtube.com/watch?v=81f3BDShvtI) ve orijinal paletin ekran görüntüsü.
Video analizi Gemini 3.7 Flash **agentic video understanding** ile iki ayrı sorguda yapıldı
(native `processing_call` / `processing_result` kayıtlarıyla doğrulandı); ses parçası konuyla
ilgisizdir, davranış yalnızca görüntüden çıkarılmıştır. Palet düzeni ve etiket metinleri
orijinal paletin ekran görüntüsünden alınmıştır.

## Palet ve akış

**ProEngineering Tools** başlıklı dar araç penceresi. Akış üç adımdır ve komut sonunda biter:

| Adım / kontrol | İşlev |
| --- | --- |
| 1. `Get tags` | Tag'ler Revit'in kendi seçimiyle (pencere) önceden seçildiyse doğrudan alınır, **Finish gerekmez**. Seçim yoksa seçim modu (Finish) açılır. Nokta seçimi yoktur. |
| 2. filtre (açılır liste) | Liste **seçimden** dolar: kategori / aile ve adetleri. Bir tür seçince `Tags count` filtrelenmiş adedi gösterir; `All selected tags` = hepsi. |
| 3. `Pick direction` | **Tek tık**: kolon başlangıç noktası, kolon dik aşağı iner; filtrelenmiş tag'ler dizilir, komut biter, filtre sıfırlanır. Düğmeye **sağ tık** veya **Ctrl+tık** ile ikinci bir yön noktası da istenir (Esc = dik aşağı). Bekleyen seçim yoksa görünümde seçili tag'leri, o da yoksa son grubu yeni noktaya taşır. |
| `Auto lanes` | Seçili (veya Get tags ile bekleyen, o da yoksa görünümdeki tüm) tag'leri, elemanlarının yayılımının çevresine otomatik türetilen kolon ve satırlara (sol/sağ kolon, üst/alt satır; kapasite yetmezse yarım adım şaşırtmalı dış halkalar) leader'lı olarak dizer. Adımlar tag ailelerinin gerçek metin ölçülerinden hesaplanır (`Spacing x` alt sınırdır), her şerit Pro Tools çekirdeğiyle yerleştirilir ve canlı ayar için ayrı grup olarak hatırlanır. Kat planlarında "tag'ler plan dışında" düzeni için. |
| `Tags count: N` | Seçilen / filtrelenen / yerleştirilen tag sayısı |
| `Spacing x:` `[0.6]` `-` `+` | Satır aralığı, metre (0,6 m = orijinal aracın 2 ft'i); yazdıkça veya -/+ ile (0.1 adım) canlı uygulanır |
| `Shift x:` `[0.30]` `-` `+` | Omuz uzunluğu: metin kenarından dirseğe, metre (0,30 m = orijinal aracın 1 ft'i); canlı uygulanır |
| durum satırı | Son işlemin özeti / hata metni |

Canlı değerler (Spacing x / Shift x) şuna uygulanır: görünümde **seçili tag varsa** o gruba
(daha önce dizilmişse aynı orijin ve yönle; hiç dizilmemişse en üstteki tag sabit kalır), seçim
yoksa **son yerleştirilen gruba**.

## Davranış (videodaki gibi)

- Yalnızca `IndependentTag` öğeleri seçilebilir (pencere/box seçim destekli).
- İlk tag başı tıklanan noktaya gelir, sonrakiler **Spacing x** aralığıyla kolon ekseni boyunca
  dizilir: `Head(i) = Origin + i × Spacing × Axis`. Varsayılan eksen görünümün aşağı yönüdür.
  **Metin kenarı hizası:** her tag ailesinin etiket geometrisi bir kez okunur (`EditFamily`,
  önbelleklenir) ve metin genişliği yazı tipinin ilerleme genişlikleriyle hesaplanır (Revit
  "Text Size" = büyük harf yüksekliği, Arial için em = boyut/0,716; aile probe'undaki etiket
  kutusu genişlikleriyle doğrulandı). Metnin elemanlara bakan kenarı kolon çizgisine oturur; böylece
  orijine ortalı (`M_Pipe/Duct Size Tag`) veya ötelenmiş (`M_Diffuser Tag`) etiketler de aynı
  hizaya gelir ve omuzlar eşit görünür. Metin bloğunun merkezi satıra oturur; Revit leader'ı
  metin kenarının orta noktasından başlattığı için satır yüksekliğindeki dirsek sıfır açılı (yatay)
  bir omuz verir. Aile okunamazsa baş kolon çizgisine konur. Çok satırlı tag'lerde **Spacing x**
  değerini metin yüksekliğinden büyük tutun.
- **Ok uçları korunur.** Taşımadan önce her leader'ın ucu "Free" yapılır ve baktığı nokta
  kaydedilir; taşımadan sonra aynı noktaya geri yazılır. Böylece ok, kullanıcının/Revit'in daha
  önce seçtiği noktada kalır (Attached uçta Revit ucu elemanın en yakın, çoğu kez görünmeyen
  noktasına kaydırıyordu). Yan etki: leader uçları "Free" olur; eleman taşınırsa ok takip etmez.
- **Sıralama ve kesişme.** Satırlar, okların baktığı noktalara göre kolon merkezinden açısal
  yelpaze şeklinde sıralanır; ardından kesişen her leader çifti takas edilir. Her takas toplam
  leader uzunluğunu kısalttığı için işlem kesişme kalmayınca durur.
- **Shift x** — her leader'ın dirseği kolon çizgisinden bu kadar uzağa, kolona dik ve **elemanlara
  doğru** yerleştirilir; dirsekler hizalı, görünen omuzlar eşit uzunluktadır. Yön otomatik seçilir
  (elemanlar kolonun hangi tarafındaysa oraya); değerin işareti yönü değiştirmez.
- Leader'lar kapalıysa açılır; tag'ler host elemanlara bağlı kalır.
- **Canlı düzeltme (post-correction)** — son dizilen grup hafızada tutulur; değer değiştikçe
  (300 ms gecikmeyle, Enter ile anında) kolon yeniden dizilir. Önceki gruplar etkilenmez.
- Kilitli 3B, plan ve kesit görünümlerinde çalışır. 3B görünümde nokta seçimi için tag'lerin
  düzleminde, ekrana paralel geçici bir çalışma düzlemi oluşturulur ve seçim sonrası silinir.
- Değerler **metre** girilir, Revit içi birimine çevrilir. Orijinal palet birim göstermez; videodaki
  kısa omuz ve sıkı aralık, orijinalin değerleri doğrudan Revit iç birimi (feet) olarak kullandığına
  işaret eder: "Spacing 2" ≈ 0,6 m, "Shift 1.00" ≈ 0,3 m. Varsayılanlar buna göre seçildi.

## Kurulum

1. Derleyin:
   ```
   dotnet build -c Release
   ```
2. Çıktıyı Revit 2027 eklenti klasörüne kopyalayın (Windows):
   ```
   %AppData%\Autodesk\Revit\Addins\2027\RevitSetTags\RevitSetTags.dll
   %AppData%\Autodesk\Revit\Addins\2027\RevitSetTags.addin
   ```
   `.addin` içindeki `<Assembly>` yolunu DLL'in tam yolu yapın (göreli `RevitSetTags.dll` de
   çalışır; DLL o zaman `.addin` ile aynı klasörde durmalıdır).
3. Revit 2027'yi başlatın → **Pro Tools** sekmesi → **Tag Tools** paneli → **Set Tags**.

> Not: Derleme .NET 10 hedefler (`net10.0-windows`); Revit 2027 çalışma zamanı .NET 10'dur ve
> Nice3point 2027.2.0 paketleri yüklü Revit 2027.2 (27.2.0.39) ile aynı API sürümünü taşır.
> `IndependentTag.SetLeaderElbow` (2022+) ve `GetTaggedLocalElements()` kullanıldığı için eski
> sürümlerde paket sürümü ve hedef çerçeveyle birlikte kod da uyarlanmalıdır.
> Revit 2027 bu makinede imzasız eklenti için güvenlik uyarısı göstermedi; farklı kurulumlarda
> ilk açılışta "Always Load" seçmeniz gerekebilir.

## Kullanım

1. Şeritte **Pro Tools → Set Tags**'e tıklayın; palet açılır.
2. Tag'leri Revit'te pencereyle seçin → **Get tags** (Finish yok).
3. Gerekirse filtreden bir tür seçin (ör. `Pipe Tags (10)`).
4. **Pick direction** → kolonun başlangıç noktasını tıklayın; tag'ler dik aşağı dizilir, filtre
   sıfırlanır. Eğik/yatay dizim için düğmeye sağ tık (veya Ctrl+tık) ve ikinci nokta.
5. Bütün bir kat planı için: tag'leri seçin (veya hiçbir şey seçmeyin) → **Auto lanes**; tag'ler
   bina çevresindeki şeritlere leader'larıyla dizilir. Büyük yazı için önce görünüm ölçeğini
   (ör. 1:200) ayarlayın; adımlar ölçeğe göre otomatik büyür.
6. İnce ayar: görünümde grubu seçili tutarak (veya seçim yoksa son grup için) **Spacing x** /
   **Shift x** değerlerini yazın veya -/+ ile değiştirin; canlı güncellenir. Her işlem tek bir
   "Order Tags" transaction'ıdır, Ctrl+Z ile geri alınabilir.

## Varsayımlar ve sınırlar

- Video 55 saniyelik ekran kaydıdır; kaynak kod gösterilmez. Dizim, sıralama ve omuz davranışı
  videodaki sonuçtan çıkarılmıştır; birim ve `Pick direction` düğmesinin tam işlevi videoda
  gösterilmediği için burada belgelenen yorumlar kullanıldı.
- Dirsek ayarı `HasLeaderElbow`/`SetLeaderElbow` destekleyen leader'lara uygulanır; düz leader
  zorunlu tag türlerinde omuz adımı sessizce atlanır, tag yine taşınır.
- 3B görünüm kilitli olmalıdır (Revit tag'leri yalnızca kilitli 3B görünümlerde tutar).

## Proje yapısı

| Dosya | Görev |
| --- | --- |
| `App.cs` | Şerit sekmesi/paneli ve düğme kaydı |
| `Commands/ShowSetTagsCommand.cs` | Modeless paleti açan komut |
| `UI/SetTagsWindow.cs` | Palet: Get tags / seçim filtresi / Pick direction / Auto lanes / Spacing x / Shift x (-/+), canlı güncelleme |
| `Handlers/SetTagsHandler.cs` | API bağlamında çalışan `IExternalEventHandler`: seçim, filtre, orijin/yön seçimi (3B için geçici çalışma düzlemi), grup hafızası, canlı düzeltme hedefi |
| `Services/TagOrderingService.cs` | Kolon yerleştirme, sıralama, leader omzu ve metin ölçüm çekirdeği |
| `Services/LaneLayoutService.cs` | Auto lanes: çevre şeritlerinin türetilmesi, kapasite/atama, şerit başına `PlaceColumn` |
| `RevitSetTags.addin` | Revit eklenti manifest'i |
