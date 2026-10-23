# Зафиксированные версии движков и их SHA256. Обновлять вместе: версия → ссылки → хеши.
$Engines = @{
    ByedpiVersion  = '0.17.3'
    SingboxVersion = '1.14.3'
    ZapretVersion  = '72.13'
    # zapret (winws + WinDivert) — только win-x64: для ARM64 нет подписанного драйвера WinDivert.
    Zapret         = @{ url = 'https://github.com/bol-van/zapret/releases/download/v72.13/zapret-v72.13.zip'
                        sha256 = 'c493e33a0dc4eba23a8686efdaba55f59755ad6ade3564aebd9d13f4c65e2e0c' }
    # Фейковые пакеты из набора Flowseal/zapret-discord-youtube (MIT), зафиксированный коммит.
    FakeBase       = 'https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/176f56dc3f11ce5915c26abcdde903f93d835b64/bin/'
    Fakes          = [ordered]@{
        'tls_clienthello_4pda_to.bin'        = 'eefeaf09dde8d69b1f176212541f63c68b314a33a335eced99a8a29f17254da8'
        'tls_clienthello_www_google_com.bin' = '936c2bee4cfb80aa3c426b2dcbcc834b3fbcd1adb17172959dc569c73a14275c'
        'tls_clienthello_www_sferum_ru.bin'  = 'e6dd8d724eecc6a7340bf64e2ebec81c60bc4e078c6f702b4b6ba9bf184180ad'
        'tls_clienthello_sochi_park.bin'     = 'ad552922acfe029521da884ea29f4bb28bb6bdcee9b1a70b46adbfd266c11d4d'
        'stun.bin'                           = '9cd5469309780ca56c0bd97266524a48c7ee529d02c3179cfecb20b260a59641'
        'stun2.bin'                          = 'b7c2497496039c541f7337ac8536813f0a1cf52363ab2faa5213b7816d458813'
        'quic_initial_www_google_com.bin'    = 'f4589c57749f956bb30538197a521d7005f8b0a8723b4707e72405e51ddac50a'
        'ACTIVE_DISCORD_UDP.bin'             = '2fe18b3bd20807d36704d0b072092ee49ae84edca907a4420ab9a0f0f28fddcf'
    }
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
