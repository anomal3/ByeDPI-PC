# Зафиксированные версии движков и их SHA256. Обновлять вместе: версия → ссылки → хеши.
$Engines = @{
    ByedpiVersion  = '0.17.3'
    SingboxVersion = '1.14.3'
    'win-x64'      = @{
        byedpi  = @{ url = 'https://github.com/hufrea/byedpi/releases/download/v0.17.3/byedpi-17.3-x86_64-w64.zip'
                     sha256 = '70d2c94147193cb915f9c6eb5144b8d404dacbcfa90bda2383b6b211afafa456' }
        singbox = @{ url = 'https://github.com/SagerNet/sing-box/releases/download/v1.14.3/sing-box-1.14.3-windows-amd64.zip'
                     sha256 = 'd8b86ef58936dba9230ec6a57ffa4243933f3c14ef4809828b6c553440ed1518' }
    }
    # Сборки byedpi под ARM64 Windows нет — x64-версия работает через эмуляцию.
    'win-arm64'    = @{
        byedpi  = @{ url = 'https://github.com/hufrea/byedpi/releases/download/v0.17.3/byedpi-17.3-x86_64-w64.zip'
                     sha256 = '70d2c94147193cb915f9c6eb5144b8d404dacbcfa90bda2383b6b211afafa456' }
        singbox = @{ url = 'https://github.com/SagerNet/sing-box/releases/download/v1.14.3/sing-box-1.14.3-windows-arm64.zip'
                     sha256 = '195b35424c6e3f5913954c7f08c5d8ba2d2199f6d18eb27a3972a5a29cdaa78c' }
    }
}
