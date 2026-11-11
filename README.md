# NavTune

Ứng dụng chỉnh âm thanh toàn hệ thống cho macOS, thuộc hệ sinh thái [navtools.vn](https://navtools.vn). Miễn phí.

NavTune nhận toàn bộ âm thanh của máy (YouTube, Chrome, Safari, Spotify…), chỉnh EQ và hiệu ứng, rồi phát ra tai nghe / loa đang dùng, kể cả Bluetooth.

**Bản Windows** (Windows 10 / 11, C# / WPF) nằm trong thư mục [`windows/`](windows/README.md): cùng bộ xử lý âm thanh, preset và định dạng file preset.

## Mới trong v1.3: tắt tiếng từng ứng dụng

Tắt tiếng một ứng dụng **ở loa / tai nghe của mình**, còn ứng dụng vẫn chạy và vẫn phát như bình thường:

- OBS vẫn livestream đủ tiếng. Âm thanh trong luồng phát do OBS tự trộn, không đi qua loa, nên có thể tắt tiếng chính OBS để khỏi nghe tiếng monitor mà không ảnh hưởng buổi live
- Phần mềm ghi màn hình và nguồn "macOS Audio Capture" của OBS (ScreenCaptureKit) vẫn thu được tiếng của ứng dụng bị tắt. Đã đo: thu đủ mức −30,5 dB của tiếng thử trong lúc tiếng đó bị chặn khỏi loa
- Bật ở trang **Ứng dụng** (nút loa hoặc chọn "Tắt tiếng ở loa") hoặc trên **thanh menu** (bấm tên ứng dụng đang phát)
- Không tốn CPU, không giữ máy thức: dùng process tap chế độ "muted" gắn vào một aggregate device riêng không bao giờ chạy IO
- Vẫn tắt tiếng khi NavTune ở chế độ chờ, khi nghe bản gốc (⌘B), khi đổi thiết bị. Tắt NavTune hoặc thoát app thì mọi ứng dụng có tiếng lại; nếu NavTune bị buộc thoát, macOS tự gỡ bộ tắt tiếng
- Bật lại tiếng thì ứng dụng về đúng chế độ trước đó (EQ chung / preset riêng / không xử lý)
- Lưu ý: macOS chặn tiếng của ứng dụng tới **mọi** thiết bị. Nếu ứng dụng phát thẳng vào thiết bị ảo (BlackHole, Loopback…) để OBS thu, tắt tiếng cũng chặn luôn đường đó

Sửa lỗi:
- Bỏ qua tiến trình đã thoát khi cập nhật tap. Trước đây trường hợp này có thể khiến chuỗi xử lý chính phải khởi động lại và tạm mất EQ
- Phản ứng ngay khi có tiến trình âm thanh mới (trước đây chờ 0,3 s)
- Ghi nhật ký khi tap không nhận được âm thanh (thường do thiếu quyền)

## Mới trong v1.2: tối ưu hiệu năng

Số đo thực tế trên Mac mini (Apple Silicon), phát qua loa Bluetooth:

| Tình huống | CPU |
|---|---|
| v1.1, cửa sổ mở, đang phát nhạc | ~40% |
| v1.2, đang phát nhạc, cửa sổ đóng hoặc bị che | **0,2–0,4%** |
| v1.2, không có gì phát (chế độ chờ) | **0%**, không chặn máy ngủ |
| Bộ xử lý âm thanh riêng (12 bộ lọc + mọi hiệu ứng, bản release) | **0,34% một nhân**, 36 µs cho mỗi khối 512 mẫu |

RAM giảm từ ~160 MB xuống ~60 MB.

- **Chế độ chờ**: không có ứng dụng nào phát tiếng trong 10 giây thì NavTune dừng IO; có tiếng là chạy lại ngay (theo thông báo của Core Audio, không thăm dò liên tục)
- Biểu đồ phổ và đồng hồ vẽ bằng Core Animation theo display link, chỉ khi cửa sổ thực sự hiện trên màn hình
- Giao diện dùng Observation: chỉ phần giao diện liên quan được vẽ lại khi dữ liệu đổi
- DSP: xử lý theo khối, dạng planar, bỏ qua bộ lọc 0 dB, khối IO chạy thẳng trên luồng thời gian thực của HAL, không cấp phát bộ nhớ và không khoá trên luồng âm thanh
- Lưu cài đặt khi dừng kéo thanh trượt; danh sách ứng dụng cập nhật theo sự kiện
- Mở lại NavTune từ Finder/Launchpad sẽ hiện cửa sổ chính
- Nhật ký hoạt động: `log show --last 1h --predicate 'subsystem == "vn.navtools.navtune"'`

## Mới trong v1.1

- **EQ riêng cho từng ứng dụng**: mỗi app dùng EQ chung, một preset riêng kèm âm lượng riêng, hoặc "Không xử lý"
- **Tự chọn preset theo nội dung**: nhận dạng giọng nói, rap, guitar điện, nhạc điện tử, dàn nhạc, kèn, piano, guitar mộc bằng bộ phân loại âm thanh có sẵn của macOS (chạy trên máy), rồi chuyển preset mượt
- **EQ tham số**: tối đa 10 băng, kéo điểm trực tiếp trên biểu đồ; 5 kiểu lọc Peak / Low shelf / High shelf / Low cut / High cut; chỉnh tần số, mức, Q
- **Tiếng Việt / English**, đổi ngay trong Cài đặt hoặc màn hình chào
- Giao diện mới: màu thương hiệu, biểu đồ phổ mượt hơn, thanh menu có preset nhanh

## Tính năng (v1.0)

**Bộ chỉnh âm**
- EQ 10 băng (32 Hz – 16 kHz), ±12 dB, kèm biểu đồ phổ âm thanh thời gian thực và đường đáp tuyến EQ
- 24 preset theo dòng nhạc: V-Pop, Bolero / Trữ tình, Ballad, Pop, Rock, Metal, EDM, Dance / Remix, Rap, R&B, K-Pop, Lo-fi, Jazz, Cổ điển, Acoustic, Piano, Giọng hát, Tăng Bass, Tăng Treble, Xem phim, Chơi game, Podcast, Nghe đêm…
- Preset riêng: lưu, đổi tên, xoá, nhập / xuất file JSON để chia sẻ

**Hiệu ứng**
- Bass, Độ trong, Không gian 3D (mở rộng stereo), Cân bằng âm lượng, Tăng âm lượng
- Bộ limiter chống vỡ tiếng luôn bật

**Thiết bị**
- Chọn thiết bị phát hoặc theo mặc định của macOS
- Nhớ cài đặt riêng cho từng thiết bị, tự đổi khi kết nối tai nghe / loa Bluetooth

**Ổn định**
- Tự khởi động lại khi mất tín hiệu, khi thiết bị đổi tần số lấy mẫu, khi máy thức dậy sau ngủ
- Nếu NavTune tắt hoặc gặp sự cố, âm thanh gốc tự trở lại bình thường

**Tiện ích**
- Biểu tượng trên thanh menu để chỉnh nhanh, cửa sổ chính để chỉnh chi tiết
- Nghe bản gốc để so sánh (⌘B), khởi động cùng máy, hiện / ẩn trên Dock, màn hình hướng dẫn lần đầu

## Yêu cầu

- macOS 15 trở lên
- Xcode / Swift 6 để build

## Build, chạy và đóng gói

```sh
./scripts/build_app.sh        # tạo build/NavTune.app
open build/NavTune.app
swift test                    # kiểm tra bộ xử lý âm thanh
./scripts/make_dmg.sh         # tạo build/NavTune-<phiên bản>.dmg
```

Lần đầu mở, bấm **Bắt đầu sử dụng**. macOS sẽ hỏi quyền **ghi âm thanh hệ thống**, bấm **Cho phép**. Nếu đã lỡ từ chối:
Cài đặt hệ thống → Quyền riêng tư & Bảo mật → Ghi màn hình & âm thanh hệ thống → bật NavTune.

## Phát hành công khai

Bản build mặc định chỉ ký tạm (ad-hoc), chạy tốt trên máy này. Máy khác sẽ bị Gatekeeper cảnh báo. Mỗi lần build lại, chữ ký ad-hoc đổi nên macOS hỏi lại quyền ghi âm thanh hệ thống, và NavTune chờ đến khi bấm **Cho phép**. Ký bằng Developer ID thì quyền được giữ qua các bản cập nhật. Để người dùng cài không bị cảnh báo:

1. Đăng ký Apple Developer Program (99 USD/năm), tạo chứng chỉ **Developer ID Application**.
2. Build và ký:
   ```sh
   SIGN_IDENTITY="Developer ID Application: <Tên> (<TEAMID>)" ./scripts/build_app.sh
   SIGN_IDENTITY="Developer ID Application: <Tên> (<TEAMID>)" ./scripts/make_dmg.sh
   ```
3. Notarize:
   ```sh
   xcrun notarytool submit build/NavTune-1.3.0.dmg --keychain-profile <profile> --wait
   xcrun stapler staple build/NavTune-1.3.0.dmg
   ```

## Cấu trúc mã

| Thư mục / file | Vai trò |
|---|---|
| `Audio/TapChain.swift` | Một chuỗi xử lý: process tap (Core Audio) + bộ xử lý + aggregate device phát ra thiết bị |
| `Audio/AudioRouter.swift` | Chia âm thanh: chuỗi chính cho EQ chung, mỗi app có preset riêng một chuỗi |
| `Audio/MuteTap.swift` | Tắt tiếng ứng dụng ở loa mà không ảnh hưởng ứng dụng, livestream, ghi hình |
| `Audio/AudioProcesses.swift` | Tìm tiến trình đang dùng âm thanh và app sở hữu nó (Chrome Helper → Chrome) |
| `Audio/ContentDetector.swift` | Nhận dạng nội dung bằng SoundAnalysis để tự chọn preset |
| `Audio/ActivityMonitor.swift` | Biết tiến trình nào đang phát tiếng (theo thông báo Core Audio) để vào / thoát chế độ chờ |
| `Model/SpectrumModel.swift` | Tính phổ theo yêu cầu khi có biểu đồ đang hiện |
| `Views/LiveLayers.swift` | Biểu đồ phổ và đồng hồ vẽ bằng Core Animation + display link |
| `Model/L10n.swift` | Song ngữ Việt / Anh |
| `Audio/AudioProcessor.swift` | Chuỗi xử lý thời gian thực: preamp → EQ 10 băng → bass / độ trong → stereo width → cân bằng âm lượng → boost → limiter |
| `Audio/Biquad.swift` | Bộ lọc peaking / shelf (RBJ cookbook) và tính đáp tuyến |
| `Audio/SpectrumAnalyzer.swift` | FFT (Accelerate) cho biểu đồ phổ |
| `Audio/AudioDevice.swift` | Liệt kê thiết bị phát |
| `Model/AppState.swift` | Trạng thái app, lưu cài đặt, theo dõi thiết bị, tự phục hồi |
| `Model/Presets.swift`, `Model/SoundSettings.swift` | Preset và cài đặt âm thanh |
| `Views/` | Giao diện: cửa sổ chính, thanh menu, hướng dẫn |
| `Tests/` | Kiểm tra bộ xử lý âm thanh |
