# NavTune cho Windows

Bản Windows của NavTune: chỉnh âm thanh toàn hệ thống cho mọi ứng dụng (YouTube, Chrome, Spotify, game…), phát ra loa, tai nghe đang dùng, kể cả Bluetooth. Cùng bộ xử lý âm thanh, cùng 24 preset và cùng định dạng file preset với bản macOS.

## Yêu cầu

- Windows 10 (1809 trở lên) hoặc Windows 11, 64-bit
- Một **thiết bị âm thanh ảo** để NavTune nhận được âm thanh của mọi ứng dụng, chọn một trong hai:
  - **VB-CABLE** (miễn phí): <https://vb-audio.com/Cable/>. Giải nén, chuột phải `VBCABLE_Setup_x64.exe` → *Run as administrator* → *Install Driver*, khởi động lại máy.
  - Hoặc thiết bị ảo của **FxSound** nếu máy đã cài FxSound. Khi dùng NavTune phải **thoát FxSound** (chuột phải biểu tượng FxSound ở khay → Exit) và tắt "khởi động cùng Windows" của FxSound, vì hai ứng dụng dùng chung một thiết bị ảo.

Không cần cài .NET: `NavTune.exe` đã chứa sẵn mọi thứ.

## Cài đặt

- **Bộ cài** `NavTune-Setup-1.3.0.exe`: cài cho tài khoản hiện tại, không cần quyền quản trị. Có lối tắt trong menu Start, tuỳ chọn tự chạy khi đăng nhập, gỡ cài đặt trong *Settings → Apps*.
- **Bản portable** `NavTune-1.3.0-win-x64.zip`: giải nén rồi chạy `NavTune.exe`, đặt ở đâu cũng được.

Lần đầu mở, chọn ngôn ngữ (Tiếng Việt / English) rồi bấm **Bắt đầu sử dụng**.

Đóng cửa sổ thì NavTune vẫn chạy ở khay hệ thống: bấm biểu tượng để chỉnh nhanh, chuột phải để bật/tắt hoặc thoát. Windows 11 thường ẩn biểu tượng của ứng dụng mới trong mục **^** ở thanh tác vụ; muốn luôn hiện thì vào *Settings → Personalization → Taskbar → Other system tray icons* và bật NavTune.

## Cách hoạt động

macOS cho phép "chặn" âm thanh của ứng dụng rồi phát lại (process tap). Windows không có cơ chế đó, nên NavTune làm như FxSound:

1. Khi NavTune **bật**, thiết bị ảo được đặt làm loa mặc định của Windows: mọi ứng dụng phát vào đó.
2. NavTune thu lại (WASAPI loopback), xử lý (EQ, hiệu ứng, limiter) rồi phát ra loa hoặc tai nghe thật.
3. Khi NavTune **tắt hoặc thoát**, loa mặc định được trả lại như cũ. Nếu NavTune bị buộc dừng (crash, End task), một tiến trình canh gác nhỏ (cũng tên NavTune, chạy nền) tự trả lại loa trong vòng 1–2 giây. Nếu máy mất điện đột ngột, loa được trả lại ở lần đăng nhập kế tiếp.

Một số điểm nên biết:

- **Phím âm lượng vẫn dùng như bình thường**: NavTune đồng bộ âm lượng và tắt tiếng giữa thiết bị ảo và loa thật (hai chiều, kể cả tai nghe Bluetooth tự chỉnh âm lượng).
- **Đổi loa ngay trong Windows** (biểu tượng loa ở thanh tác vụ) cũng được: NavTune tự chuyển sang thiết bị vừa chọn. Rút tai nghe hay tắt loa Bluetooth thì NavTune chuyển sang thiết bị còn lại, và tự quay về khi thiết bị cũ kết nối lại.
- **Độ trễ** thêm khoảng 65 ms (mức Cân bằng). Chơi game có thể chọn Thấp (≈ 45 ms) trong trang *Thiết bị*; nghe lụp bụp thì chọn An toàn.
- **Chế độ chờ**: không có gì phát trong 10 giây thì NavTune ngừng phát ra loa (gần 0% CPU, loa và tai nghe được nghỉ), có tiếng là chạy lại ngay.
- Hai đồng hồ của thiết bị ảo và loa thật lệch nhau vài chục ppm. NavTune bù bằng bộ đổi tần số lấy mẫu của chính Windows, nên chạy cả ngày không trễ dần và không bị giật.
- Ứng dụng tự chọn một loa cụ thể (không theo loa mặc định) hoặc dùng chế độ độc quyền (exclusive) thì không đi qua NavTune.
- Thiết bị liên lạc mặc định (Zoom, Teams, Discord…) được giữ nguyên.

## Tính năng

| | macOS | Windows |
|---|---|---|
| EQ đồ hoạ 10 băng, EQ tham số 10 băng (Peak / Low shelf / High shelf / Low cut / High cut) | ✓ | ✓ |
| 24 preset theo dòng nhạc, preset riêng, nhập / xuất JSON (dùng chung file với bản Mac) | ✓ | ✓ |
| Bass, Độ trong, Không gian 3D, Cân bằng âm lượng, Tăng âm lượng, limiter chống vỡ tiếng | ✓ | ✓ |
| Nghe bản gốc để so sánh | ⌘B | Ctrl+B |
| Nhớ cài đặt riêng cho từng thiết bị, tự đổi khi đổi loa / tai nghe | ✓ | ✓ |
| Biểu đồ phổ thời gian thực, đồng hồ mức | ✓ | ✓ |
| Chế độ chờ khi không có gì phát, tự phục hồi khi mất tín hiệu hoặc sau khi máy ngủ | ✓ | ✓ |
| Thanh menu / khay hệ thống với preset nhanh, Bass, Tăng âm lượng, tắt tiếng nhanh | ✓ | ✓ |
| Tắt tiếng từng ứng dụng | ✓ | ✓ (*) |
| Âm lượng riêng từng ứng dụng | theo preset riêng | ✓ (thanh trượt 0–100%) |
| EQ / preset riêng cho từng ứng dụng | ✓ | — |
| Tự chọn preset theo nội dung | ✓ (SoundAnalysis của Apple) | ✓ (YAMNet của Google, chạy ngay trên máy) |
| Tiếng Việt / English, khởi động cùng máy, giao diện sáng / tối | ✓ | ✓ |

(*) Trên Windows, tắt tiếng một ứng dụng dùng cơ chế của Volume Mixer, nên phần mềm thu âm thanh hệ thống (OBS…) cũng không thu được tiếng của ứng dụng đó. Tắt NavTune thì mọi ứng dụng có tiếng lại. Ứng dụng đã đóng thì được bật tiếng lại ngay lần phát kế tiếp.

### Tự chọn preset theo nội dung

Bật công tắc **Tự chọn preset theo nội dung** ở trang *Bộ chỉnh âm* (hoặc ở cửa sổ khay). NavTune nghe thứ đang phát và tự chuyển preset, giống bản macOS:

| Nghe thấy | Preset |
|---|---|
| Lời nói (podcast, phim, video nói chuyện) | Podcast / Lời nói |
| Rap / hip-hop | Rap / Hip-hop |
| Guitar điện, rock, metal | Rock |
| Synth, nhạc điện tử (EDM, house, techno…) | EDM |
| Dàn nhạc, đàn dây, nhạc cổ điển | Cổ điển |
| Kèn saxophone / trumpet, jazz | Jazz |
| Piano | Piano |
| Guitar mộc, ukulele, folk | Acoustic |
| Nhạc khác (có lời hoặc không rõ thể loại) | Pop |

- Bản macOS dùng bộ nhận dạng âm thanh có sẵn của Apple. Windows không có thứ tương đương, nên NavTune mang theo **YAMNet**, mô hình nhận dạng 521 loại âm thanh (AudioSet) của Google, viết lại bằng C# và chạy ngay trên máy: không cần mạng, không gửi âm thanh đi đâu.
- NavTune nghe âm thanh gốc (trước khi qua EQ), phân tích 1 giây gần nhất sau mỗi 0,75 giây và ra kết luận khoảng 1,5 giây một lần. Như bản macOS, chỉ đổi preset khi **3 lần liên tiếp** cùng một kết luận (khoảng 5 giây), rồi chuyển êm sang đường EQ mới trong 1,2 giây.
- YAMNet chấm thể loại / nhạc cụ thấp hơn bộ nhận dạng của Apple, nên bản Windows gộp các nhãn cùng nhóm (ví dụ *Rock music*, *Grunge*, *Electric guitar*… cho Rock) và thêm hai luật để không nhảy preset giữa bài. Một là Pop (khi chưa nghe ra phong cách nào) cần 6 lần liên tiếp. Hai là đã nhận ra một phong cách (Rock, Piano…) thì **giữ suốt bài**, kể cả đoạn nhẹ, và chỉ đổi khi nghe ra phong cách khác, có lời nói, nhạc ngừng (hết bài) hoặc 30 giây liền không còn dấu hiệu nào của nó.
- Thử trên nhạc thật: rock, jazz big band, rap, piano solo, giao hưởng, guitar mộc và giọng đọc đều ra đúng preset. Bài không nổi bật nhạc cụ hay thể loại nào thì dùng Pop.
- Rất nhẹ: khoảng 3 ms cho mỗi lần phân tích (dưới 0,5% một nhân CPU). Mô hình chỉ được nạp (khoảng 15 MB RAM) khi bật công tắc.
- Tự chọn preset khác, kéo thanh EQ hay chuyển sang EQ tham số thì chế độ tự động tắt (như bản macOS). Khi đang *Nghe bản gốc* thì NavTune tạm không phân tích.

## Xử lý sự cố

- **Không có tiếng**: mở NavTune và xem dòng thông báo màu cam ở đầu trang. Nó cho biết nguyên nhân (thiếu thiết bị ảo, FxSound đang chạy, loa bị ứng dụng khác chiếm…) và có nút sửa.
- **Trả loa mặc định về như cũ ngay lập tức**: thoát NavTune (chuột phải biểu tượng ở khay → Thoát NavTune). Nếu NavTune đã bị tắt đột ngột mà loa chưa về, chạy `NavTune.exe --restore-audio` hoặc chọn loa trong *Settings → System → Sound*.
- **Thoát NavTune từ dòng lệnh**: `NavTune.exe --quit`.
- **Nhật ký**: `%LOCALAPPDATA%\NavTune\navtune.log` (*Cài đặt → Mở thư mục nhật ký*). Cài đặt và preset nằm ở `%APPDATA%\NavTune\settings.json`.

## Build từ mã nguồn

Cần .NET 10 SDK (và Inno Setup 6 nếu muốn tạo bộ cài):

```powershell
cd windows
./build.ps1                 # chạy test, tạo dist\NavTune-<ver>-win-x64\NavTune.exe, file .zip và bộ cài
./build.ps1 -Runtime win-arm64 -NoInstaller
dotnet test tests/NavTune.Tests   # 54 kiểm tra: bộ xử lý âm thanh (port từ bản macOS), bộ đệm, bù lệch xung nhịp, khôi phục, nhận dạng nội dung
```

Chạy `NavTune.exe --screenshots <thư mục> [vi|en] [light|dark]` để xuất ảnh mọi trang giao diện. Chế độ này dùng cài đặt tạm và không đụng tới thiết bị âm thanh.

## Cấu trúc mã

| Thư mục / file | Vai trò |
|---|---|
| `src/NavTune.Core/Audio/AudioProcessor.cs` | Chuỗi DSP thời gian thực, port từ bản macOS: preamp → EQ → bass / độ trong → stereo width → cân bằng âm lượng → boost → limiter |
| `src/NavTune.Core/Audio/Biquad.cs`, `SpectrumAnalyzer.cs` | Bộ lọc RBJ, đáp tuyến, FFT cho biểu đồ phổ |
| `src/NavTune.Core/Audio/AudioEngine.cs` | Luồng âm thanh MMCSS: thu loopback thiết bị ảo → DSP → phát ra loa thật, chế độ chờ, bù lệch xung nhịp (`IAudioClockAdjustment`) |
| `src/NavTune.Core/Audio/FrameRing.cs` | Bộ đệm vòng giữa hai thiết bị và bộ điều khiển PI giữ độ trễ ổn định |
| `src/NavTune.Core/Audio/VolumeMirror.cs` | Đồng bộ âm lượng / tắt tiếng giữa thiết bị ảo và loa thật |
| `src/NavTune.Core/Audio/AppSessions.cs` | Liệt kê ứng dụng đang dùng âm thanh, tắt tiếng / âm lượng riêng |
| `src/NavTune.Core/Audio/YamNet.cs` | YAMNet (Google) viết lại bằng C#: log-mel spectrogram như `features.py`, mạng MobileNet như `yamnet.py`, tính bằng SIMD |
| `src/NavTune.Core/Audio/ContentDetector.cs`, `Resampler.cs` | Nghe âm thanh gốc trên luồng nền, đổi về 16 kHz, quy tắc chọn preset (port từ `ContentDetector.swift`) |
| `src/NavTune.Core/Resources/` | Trọng số YAMNet (float16, gộp batch norm) và tên 521 lớp âm thanh của AudioSet |
| `src/NavTune.Core/Audio/AudioDevices.cs`, `DeviceMonitor.cs` | Liệt kê thiết bị, đổi loa mặc định, theo dõi cắm / rút |
| `src/NavTune.Core/Interop/CoreAudio.cs` | Khai báo COM của Core Audio (đối chiếu với Win32 metadata của Microsoft) |
| `src/NavTune.Core/Model/` | Cài đặt, preset, quy tắc ứng dụng, song ngữ, lưu trữ, khôi phục sau sự cố (`Recovery.cs`) |
| `src/NavTune/ViewModels/AppState.cs` | Trạng thái và hành vi của app (tương đương `AppState.swift`) |
| `src/NavTune/Views/`, `Controls/` | Giao diện WPF (Fluent, sáng / tối), biểu đồ phổ, thanh trượt EQ, cửa sổ khay |
| `tests/NavTune.Tests/` | Kiểm tra bộ xử lý âm thanh và bộ đệm |
| `installer/` | Bộ cài Inno Setup (cài theo người dùng, có tiếng Việt) |
| `tools/IconGen/` | Vẽ `NavTune.ico` từ cùng thiết kế với icon bản Mac |
| `tools/yamnet/convert_yamnet.py` | Chuyển `yamnet.h5` của Google thành `yamnet.bin`, kèm bản NumPy của YAMNet để đối chiếu (cần `numpy`, `h5py`) |

## Giấy phép bên thứ ba

- **YAMNet** (mô hình và trọng số) © Google, giấy phép Apache 2.0: <https://github.com/tensorflow/models/tree/master/research/audioset/yamnet>.
- Tên các lớp âm thanh lấy từ **AudioSet ontology** © Google, giấy phép CC BY 4.0: <https://research.google.com/audioset/>.

Chi tiết trong [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) (đi kèm bộ cài và bản portable).
