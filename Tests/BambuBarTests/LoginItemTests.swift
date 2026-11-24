import ServiceManagement
import XCTest

final class LoginItemTests: XCTestCase {
    func testNeverRegisteredCopyShowsOpenAtLoginAsOff() {
        XCTAssertEqual(LoginItem.status(for: .notFound), .disabled)
        XCTAssertEqual(LoginItem.status(for: .notRegistered), .disabled)
    }

    func testEnabledAndPendingApprovalRemainDistinct() {
        XCTAssertEqual(LoginItem.status(for: .enabled), .enabled)
        XCTAssertEqual(LoginItem.status(for: .requiresApproval), .requiresApproval)
    }
}
