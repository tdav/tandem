# Реализация зашифрованного хранилища Tandem

> Для выполнения применить superpowers:subagent-driven-development. Независимые участки реализуются параллельно по общим контрактам.

**Цель** — дать Tandem переносимое зашифрованное хранилище записей FTP, FTPS и SFTP/SSH.

**Спецификация** — [дизайн и двоичный формат v1](../specs/2026-11-21-credential-vault-design.md).

**Стек** — .NET 11, Avalonia, стандартная криптография .NET и xUnit. Новых зависимостей нет.

## Общие контракты

- `Tandem.Net.Connections.CredentialVault(string directory) : IDisposable`. Константы `FileName = "credentials.tandemvault"`, `MaxKeyBytes = 1048576`. Свойства `IsConfigured`, `IsUnlocked`. Методы `Task CreateAsync(string password, IReadOnlyList<VaultEntry> entries, CancellationToken ct)`, `Task UnlockAsync(string password, CancellationToken ct)`, `Task<IReadOnlyList<VaultEntry>> ReadAsync(CancellationToken ct)`, `Task UpsertAsync(VaultEntry entry, CancellationToken ct)`, `Task RemoveAsync(Guid id, CancellationToken ct)`. Тип `VaultEntry(ConnectionProfile Profile, string? Password, byte[]? PrivateKey, string? Passphrase)` с безопасным `ToString()`. `CredentialVaultException : IOException` с безопасным текстом для вызывающей стороны. Реализация не меняет исходный файл при ошибке.
- `NetConnections` принимает необязательный последний параметр `CredentialVault? vault = null`. Публичный интерфейс для окна: `bool VaultConfigured`, `bool VaultUnlocked`, `Task EnableVaultAsync(string password, CancellationToken ct)`, `Task UnlockVaultAsync(string password, CancellationToken ct)`, `Task<bool> HasStoredPrivateKeyAsync(Guid id, CancellationToken ct)`. `Load()` в закрытом режиме отдаёт пустой список. После удачной разблокировки или настройки публикует список и вызывает существующее событие `Changed`.
- `Composition` передаёт `new CredentialVault(stateDirectory)` в сервис. Интерфейс открывает настройку в менеджере подключений. При существующем закрытом файле открытие менеджера сначала спрашивает мастер-пароль. Для тестов компоненты принимают временную директорию и поддельную связку.

## 1. Формат и запись

- [x] Написать тесты круглого цикла, отказа при неверном пароле, подмены заголовка или шифртекста, прав файла, параллельных записей, повторного создания и обнаружения устаревшего экземпляра.
- [x] Реализовать контракт `CredentialVault` в новых файлах в `src/Tandem.Net/Connections`. Проверить ограничение длины до выделения буфера и очистку чувствительных байтовых буферов. Не выдавать изменяемые ссылки на сохранённые байты ключей.
- [x] Запустить целевые тесты `FullyQualifiedName~CredentialVault`.

## 2. Источник данных приложения

- [x] Добавить тесты переноса, отмены и отказа, открытия без старого файла ключа, обновления, удаления и отсутствия записей в legacy-хранилища после переключения.
- [x] Подключить сервис в `NetConnections.cs`, сохранив старый режим до настройки. Использовать существующую сериализацию операций и уведомления. Учитывать незавершённые сохранения секретов после логина и их гонку с миграцией и изменением профиля.
- [x] Запустить тесты `Connections` и `SshConsoles`.

## 3. Интерфейс и подключение

- [x] Добавить диалог создания и разблокировки с замаскированным вводом, подтверждением при создании, понятной ошибкой и поддержкой отмены. Ссылаться на фактический путь файла в подсказке. Включить русский и английский ресурсы.
- [x] Связать диалог с менеджером и командой открытия. После миграции позволить редактировать профиль с ключом, который уже находится в хранилище. При закрытом хранилище предложить разблокировку из менеджера.
- [x] Добавить headless-проверки доступности и отказа с неверным паролем. Проверить интеграцию с `Composition`.

## 4. Интеграция

- [x] Сверить двоичный формат с реализацией и запустить независимое ревью хранения и маршрутов записи. Проверить, что перенос и сохранение не затрагивают реальные данные пользователя.
- [x] Обновить README с путём и границей совместимости. Выполнить `dotnet build Tandem.slnx`, `dotnet test Tandem.slnx` и `git diff --check`.
