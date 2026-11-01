import CoreMedia
import XCTest

/// NAL units from a 32×32 H.264 (baseline) and a 64×64 H.265 stream made with ffmpeg, SEI removed.
enum Fixtures {
    static let h264SPS = bytes("6742c00ad9096c044000000300400000030103c48992")
    static let h264PPS = bytes("68cb83cb20")
    static let h264IDR = bytes("658884053c98a0002053275d78")
    static let h264P = bytes("419a38097960")
    static let h265VPS = bytes("40010c01ffff01600000030090000003000003001e928090")
    static let h265SPS = bytes("42010101600000030090000003000003001ea0208105964a924caf0168080000030008000003001040")
    static let h265PPS = bytes("4401c172b42240")
    static let h265IDR = bytes("2801af78ebaf3affac95f2d3c0")
    static let h265P = bytes("0201d0097e10c6f0f57c")

    static func bytes(_ hex: String) -> [UInt8] {
        var result: [UInt8] = []
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            result.append(UInt8(hex[index..<next], radix: 16)!)
            index = next
        }
        return result
    }
}

final class VideoDepacketizerTests: XCTestCase {
    private var sequence: UInt16 = 100

    /// Pushes one packet with the next sequence number.
    private func push(_ d: VideoDepacketizer, _ payload: [UInt8], at timestamp: UInt32, marker: Bool = false,
                      skip: UInt16 = 0) -> [VideoDepacketizer.Frame] {
        sequence &+= 1 &+ skip
        return d.push(payload, sequence: sequence, timestamp: timestamp, marker: marker)
    }

    private func contents(_ sample: CMSampleBuffer) -> [UInt8] {
        let block = CMSampleBufferGetDataBuffer(sample)!
        var bytes = [UInt8](repeating: 0, count: CMBlockBufferGetDataLength(block))
        XCTAssertEqual(CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: bytes.count, destination: &bytes), noErr)
        return bytes
    }

    private func avcc(_ nals: [UInt8]...) -> [UInt8] {
        nals.flatMap { nal -> [UInt8] in
            let length = withUnsafeBytes(of: UInt32(nal.count).bigEndian) { [UInt8]($0) }
            return length + nal
        }
    }

    private func isNotSync(_ sample: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[String: Any]]
        else { return false }
        return attachments.first?[kCMSampleAttachmentKey_NotSync as String] as? Bool == true
    }

    func testH264SingleNALUnits() throws {
        let d = VideoDepacketizer(codec: .h264, parameterSets: [])
        XCTAssertTrue(push(d, Fixtures.h264SPS, at: 1000).isEmpty)
        XCTAssertTrue(push(d, Fixtures.h264PPS, at: 1000).isEmpty)
        let key = push(d, Fixtures.h264IDR, at: 1000, marker: true)
        XCTAssertEqual(key.count, 1)
        XCTAssertTrue(try XCTUnwrap(key.first).isKeyframe)
        XCTAssertEqual(try XCTUnwrap(d.format).dimensionsText, "32×32")
        // Parameter sets live in the format description, not in the sample.
        XCTAssertEqual(contents(key[0].sample), avcc(Fixtures.h264IDR))
        XCTAssertFalse(isNotSync(key[0].sample))

        let delta = push(d, Fixtures.h264P, at: 4000, marker: true)
        XCTAssertEqual(delta.count, 1)
        XCTAssertFalse(delta[0].isKeyframe)
        XCTAssertEqual(contents(delta[0].sample), avcc(Fixtures.h264P))
        XCTAssertTrue(isNotSync(delta[0].sample))
    }

    func testH264STAPAndFUA() throws {
        let d = VideoDepacketizer(codec: .h264, parameterSets: [])
        var stap: [UInt8] = [24]
        for set in [Fixtures.h264SPS, Fixtures.h264PPS] { stap += [0, UInt8(set.count)] + set }
        XCTAssertTrue(push(d, stap, at: 1000).isEmpty)
        let idr = Fixtures.h264IDR
        let indicator = (idr[0] & 0xE0) | 28
        let type = idr[0] & 0x1F
        XCTAssertTrue(push(d, [indicator, 0x80 | type] + idr[1..<5], at: 1000).isEmpty)
        XCTAssertTrue(push(d, [indicator, type] + idr[5..<9], at: 1000).isEmpty)
        let frames = push(d, [indicator, 0x40 | type] + idr[9...], at: 1000, marker: true)
        XCTAssertEqual(frames.count, 1)
        XCTAssertTrue(frames[0].isKeyframe)
        XCTAssertEqual(contents(frames[0].sample), avcc(idr))
    }

    func testFrameEndsWhenTheTimestampChangesWithoutAMarker() {
        let d = VideoDepacketizer(codec: .h264, parameterSets: [Fixtures.h264SPS, Fixtures.h264PPS])
        XCTAssertTrue(push(d, Fixtures.h264IDR, at: 1000).isEmpty)
        let frames = push(d, Fixtures.h264P, at: 4000)
        XCTAssertEqual(frames.count, 1)
        XCTAssertTrue(frames[0].isKeyframe)
    }

    func testFramesBeforeTheFirstKeyframeAreDropped() {
        let d = VideoDepacketizer(codec: .h264, parameterSets: [Fixtures.h264SPS, Fixtures.h264PPS])
        XCTAssertTrue(push(d, Fixtures.h264P, at: 1000, marker: true).isEmpty)
        XCTAssertEqual(push(d, Fixtures.h264IDR, at: 4000, marker: true).count, 1)
    }

    func testALostPacketDropsTheFrame() {
        let d = VideoDepacketizer(codec: .h264, parameterSets: [Fixtures.h264SPS, Fixtures.h264PPS])
        let idr = Fixtures.h264IDR
        let indicator = (idr[0] & 0xE0) | 28
        XCTAssertTrue(push(d, [indicator, 0x85] + idr[1..<5], at: 1000).isEmpty)
        XCTAssertTrue(push(d, [indicator, 0x45] + idr[9...], at: 1000, marker: true, skip: 1).isEmpty)
        // The next whole keyframe plays.
        XCTAssertEqual(push(d, idr, at: 4000, marker: true).count, 1)
    }

    func testH265FragmentationUnits() throws {
        let d = VideoDepacketizer(codec: .h265, parameterSets: [Fixtures.h265VPS, Fixtures.h265SPS, Fixtures.h265PPS])
        let idr = Fixtures.h265IDR
        let type = (idr[0] >> 1) & 0x3F
        let header: [UInt8] = [(idr[0] & 0x81) | (49 << 1), idr[1]]
        XCTAssertTrue(push(d, header + [0x80 | type] + idr[2..<7], at: 1000).isEmpty)
        let frames = push(d, header + [0x40 | type] + idr[7...], at: 1000, marker: true)
        XCTAssertEqual(frames.count, 1)
        XCTAssertTrue(frames[0].isKeyframe)
        XCTAssertEqual(try XCTUnwrap(d.format).dimensionsText, "64×64")
        XCTAssertEqual(contents(frames[0].sample), avcc(idr))

        let delta = push(d, Fixtures.h265P, at: 4000, marker: true)
        XCTAssertEqual(delta.count, 1)
        XCTAssertFalse(delta[0].isKeyframe)
    }

    func testH265AggregationPacketCarriesParameterSets() throws {
        let d = VideoDepacketizer(codec: .h265, parameterSets: [])
        let sets = [Fixtures.h265VPS, Fixtures.h265SPS, Fixtures.h265PPS]
        var ap: [UInt8] = [48 << 1, 1]
        for set in sets { ap += [0, UInt8(set.count)] + set }
        XCTAssertTrue(push(d, ap, at: 1000).isEmpty)
        XCTAssertEqual(push(d, Fixtures.h265IDR, at: 1000, marker: true).count, 1)
        XCTAssertNotNil(d.format)
    }
}
