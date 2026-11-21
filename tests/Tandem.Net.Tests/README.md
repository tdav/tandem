# Tandem.Net.Tests

By default `dotnet test` opens no connection outside `127.0.0.1`, starts no `sshd` and does not touch the keychain.
Network tests listen only on `127.0.0.1`, on a port the system gives out (port 0). Everything a test writes to disk is
in a temporary folder.

The checks against a real server or the real keychain run only when these environment variables are set; without them
they report `Skipped` with the reason.

| Variable | Turns on |
|---|---|
| `TANDEM_TEST_FTP_URL` (+ `TANDEM_TEST_FTP_PASSWORD`) | the FTP provider contract suite against that real FTP server |
| `TANDEM_TEST_FTPS_URL` (+ `TANDEM_TEST_FTPS_PASSWORD`) | the TLS handshake and the provider suite against that real FTPS server. Set `TANDEM_TEST_FTPS_TRUST=1` only for a test server with a self-signed certificate |
| `TANDEM_TEST_SFTP_URL` (+ `TANDEM_TEST_SFTP_PASSWORD`, or `TANDEM_TEST_SFTP_KEY` and `TANDEM_TEST_SFTP_PASSPHRASE`) | the contract suite, "archive → server" and the real shell (console) against that SFTP server |
| `TANDEM_TEST_LOCAL_SSHD=1` | the tests start the system's `/usr/sbin/sshd` as the current user on `127.0.0.1`, with keys in a temporary folder (no system setting changes, Remote Login is not turned on), and run the SFTP and console checks against it |
| `TANDEM_TEST_LARGE=1` | progress and cancellation checks using a sparse 2 GiB file against each configured test server |
| `TANDEM_TEST_KEYCHAIN=1` | write, read, replace and delete in the real keychain under the service name `Tandem.Tests.<guid>`, cleaned up afterwards (in `Tandem.Platform.Mac.Tests`) |

Use a writable test directory in the URL. Each file test creates and removes its own `tandem-test-<guid>`
subdirectory. URLs use `ftp://user@host:port/path`, `ftpes://…` (explicit TLS), `ftps://…` (implicit TLS),
or `sftp://…`. Supply passwords through the separate variables. A live SFTP run accepts the first host key
seen by each test and rejects changes during that test.
