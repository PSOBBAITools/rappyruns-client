-- Pin Share
-- 地面にピンを立てると、同じチャンネルに参加しているパーティーメンバーの画面にも
-- 同じ位置にピンが表示される。地面に引いた矢印も同じように共有する。
--
-- アドオンからは通信できないため、常駐の Rappy Runs Client (src/pinshare.lisp が
-- 中継を兼ねる) とファイルでやり取りする。このファイルは Rappy Runs Client に同梱され、
-- Pin Share を有効にするとクライアントがゲームの addons\Pin Share に配置・更新する。
--   exchange/out.txt : アドオン -> クライアント。1 行 1 命令 (<seq>\t<命令>\t<引数...>)
--   exchange/in.txt  : クライアント -> アドオン。ピン一覧と接続状態 (1 秒ごとに更新)
-- ピンの正本は中継サーバーが持ち、期限切れの削除もサーバーが行う。
-- 例外はピンセット (サイトで選んだそのクエスト用の保存済みピン): クライアントが
-- 負の ID と locked 印を付けて in.txt に混ぜる。動かせず消せず、サーバーには送らない。
--
-- 構造は Key Timer / Monster Reader (Wave Marker) と同じ作法に従う。

local lib_helpers = require("solylib.helpers")
local lib_characters = require("solylib.characters")
local lib_menu = require("solylib.menu")
local core_mainmenu = require("core_mainmenu")

local optionsFileName = "addons/Pin Share/options.lua"
local outFileName = "addons/Pin Share/exchange/out.txt"
local inFileName = "addons/Pin Share/exchange/in.txt"

local optionsLoaded, options = pcall(require, "Pin Share.options")
if not optionsLoaded or type(options) ~= "table" then
    options = {}
end
options.enable        = lib_helpers.NotNilOrDefault(options.enable, true)
options.cursorKey     = options.cursorKey or 117      -- 既定: F6 (カーソル位置にピン)
options.feetKey       = options.feetKey or 118        -- 既定: F7 (足元にピン)
options.clearKey      = options.clearKey or 119       -- 既定: F8 (自分のピンを全消去)
-- カーソル位置にピンを立てるマウスボタン (ImGui の番号: 0 = 使わない, 1 = 右, 2 = 中)。
-- 既定は右クリック。中クリックはホイールのスクロールと誤操作しやすいため。
options.mouseButton   = options.mouseButton or 1
options.middleClick   = nil   -- 旧設定 (中クリックの on/off)。mouseButton に置き換え
-- このキーを押しながら mouseButton でドラッグすると矢印を引く。既定: Shift。0 = 矢印を使わない
options.arrowKey      = options.arrowKey or 16
-- このキーを押しながら mouseButton でピン・矢印をクリックすると消す。既定: Shift。0 = クリックでは消さない。
-- arrowKey と同じキーでもよい (ピン・矢印の上なら消し、何もない地面なら矢印を引く)
options.deleteKey     = options.deleteKey or 16
options.ttl           = options.ttl or 60             -- ピンの寿命 (秒)。0 = 消すまで残す
options.maxPins       = options.maxPins or 3          -- 自分のピンの上限数。超えると古い順に消える。0 = 無制限
options.label         = options.label or ""
options.showDistance  = lib_helpers.NotNilOrDefault(options.showDistance, true)
options.clampToEdge   = lib_helpers.NotNilOrDefault(options.clampToEdge, true)
options.hideWhenMenu  = lib_helpers.NotNilOrDefault(options.hideWhenMenu, true)
options.maxCursorDist = options.maxCursorDist or 3000 -- カーソルピンの最大距離 (ゲーム内単位)
options.fontScale     = options.fontScale or 1.0
options.orderMode     = options.orderMode or 1    -- ピンの番号: 1 = 持ち主ごと, 3 = 部屋ごと, 2 = 全員通し, 0 = 出さない
options.orderMax      = options.orderMax or 0     -- ピンの番号の最大値。超えたら 0 に戻す。0 = 上限なし
options.moveOthers    = lib_helpers.NotNilOrDefault(options.moveOthers, false)  -- 他の人のピン・矢印もドラッグで動かせる・クリックで消せる
options.pinFilter     = options.pinFilter or 0    -- 表示するピン・矢印: 0 = 全部, 1 = 今いる部屋に置かれたもの, 2 = 自分のもの
options.filterKey     = options.filterKey or 0    -- pinFilter を切り替えるキー。既定: 割り当てなし
options.colorMode     = options.colorMode or 1        -- 自分のピンの色: 1 = 部屋に入った順番の色, 2 = customColor
options.customColor   = options.customColor or 0xFF8C00  -- 自分で選んだ色 (0xRRGGBB)
-- 自分の矢印の色: 0 = ピンと同じ色, 1 = 部屋に入った順番の色, 2 = arrowCustomColor
options.arrowColorMode   = options.arrowColorMode or 0
options.arrowCustomColor = options.arrowCustomColor or 0x66E0FF
options.frontKey      = options.frontKey or 0     -- キャラの正面にピンを立てるキー。既定: 割り当てなし
options.frontDist     = options.frontDist or 100  -- 正面ピンの距離 (ゲーム内単位。10 = 1m)
-- コントローラーの割り当て。pad<操作> がボタン (0 = なし)、pad<操作>Mod はそれと同時に押しておくボタン
-- (0 = 単独)。値は XInput のボタンのビット、LT = 0x10000、RT = 0x20000 (pinshare-input.dll と同じ)。
-- 並び順が DLL に渡す割り当て番号 (poll が返すビットの位置) になる
local padActionKeys = { "padFeet", "padClear", "padFilter", "padFront" }
for _, name in ipairs(padActionKeys) do
    options[name] = options[name] or 0
    options[name .. "Mod"] = options[name .. "Mod"] or 0
end

local inputDirty = true  -- 割り当てが変わった (pinshare-input.dll に渡し直す)

local function SaveOptions()
    inputDirty = true
    local file = io.open(optionsFileName, "w")
    if file ~= nil then
        io.output(file)
        io.write("return {\n")
        io.write(string.format("    enable = %s,\n", tostring(options.enable)))
        io.write(string.format("    cursorKey = %d,\n", options.cursorKey))
        io.write(string.format("    feetKey = %d,\n", options.feetKey))
        io.write(string.format("    clearKey = %d,\n", options.clearKey))
        io.write(string.format("    mouseButton = %d,\n", options.mouseButton))
        io.write(string.format("    arrowKey = %d,\n", options.arrowKey))
        io.write(string.format("    deleteKey = %d,\n", options.deleteKey))
        io.write(string.format("    ttl = %d,\n", options.ttl))
        io.write(string.format("    maxPins = %d,\n", options.maxPins))
        io.write(string.format("    label = %q,\n", options.label))
        io.write(string.format("    showDistance = %s,\n", tostring(options.showDistance)))
        io.write(string.format("    clampToEdge = %s,\n", tostring(options.clampToEdge)))
        io.write(string.format("    hideWhenMenu = %s,\n", tostring(options.hideWhenMenu)))
        io.write(string.format("    maxCursorDist = %d,\n", options.maxCursorDist))
        io.write(string.format("    fontScale = %s,\n", tostring(options.fontScale)))
        io.write(string.format("    orderMode = %d,\n", options.orderMode))
        io.write(string.format("    orderMax = %d,\n", options.orderMax))
        io.write(string.format("    moveOthers = %s,\n", tostring(options.moveOthers)))
        io.write(string.format("    pinFilter = %d,\n", options.pinFilter))
        io.write(string.format("    filterKey = %d,\n", options.filterKey))
        io.write(string.format("    colorMode = %d,\n", options.colorMode))
        io.write(string.format("    customColor = 0x%06X,\n", options.customColor))
        io.write(string.format("    arrowColorMode = %d,\n", options.arrowColorMode))
        io.write(string.format("    arrowCustomColor = 0x%06X,\n", options.arrowCustomColor))
        io.write(string.format("    frontKey = %d,\n", options.frontKey))
        io.write(string.format("    frontDist = %d,\n", options.frontDist))
        for _, name in ipairs(padActionKeys) do
            io.write(string.format("    %s = 0x%X,\n", name, options[name]))
            io.write(string.format("    %sMod = 0x%X,\n", name, options[name .. "Mod"]))
        end
        io.write("}\n")
        io.close(file)
    end
end

-- ---------------------------------------------------------------------------
-- メモリアドレス
-- ---------------------------------------------------------------------------
local _CameraPosX      = 0x00A48780
local _CameraPosY      = 0x00A48784
local _CameraPosZ      = 0x00A48788
local _CameraDirX      = 0x00A4878C
local _CameraDirY      = 0x00A48790
local _CameraDirZ      = 0x00A48794
local _CameraZoomLevel = 0x009ACEDC
local _Episode         = 0x00A9B1C8   -- u16: 0 = Ep1, 1 = Ep2, 2 = Ep4
local _MapNumber       = 0x00AAFC9C   -- u16: エリアごとに固有のマップ番号 (Room Timer と同じ)
local _PlayerMyIndex   = 0x00A9C4F4   -- u32: 自分のスロット番号 (0 始まり。部屋に入った順番)
-- プレイヤーのアドレスからのオフセット (Monster Reader / Room Timer と同じ)
local _PlayerRoom1     = 0x28         -- u16: エリア内で今いる部屋 (Room ID)
local _PlayerRoom2     = 0x2E         -- u16: 部屋の境目にいるときのもう一方の部屋
local _PlayerFacing    = 0x60         -- u16: 向き (65536 = 1 周。正面は (sin, cos) 方向、psobb-camera と同じ)

-- ---------------------------------------------------------------------------
-- エリア
-- フロア番号 (GetCurrentFloorSelf) はエピソード・クエスト内の何階目かでしかなく、
-- Ep1 の森1 と Ep2 の Temple Alpha がどちらも 1 になる。
-- そこでエピソード・マップ番号・フロア番号を 1 つの整数にまとめてピンの所属エリアにする。
-- サーバーはこの値を整数としてそのまま保存・配信する (旧形式の「フロア番号」欄をそのまま使う)。
-- ---------------------------------------------------------------------------
local function currentArea()
    local episode = pso.read_u16(_Episode) % 100
    local map = pso.read_u16(_MapNumber) % 100
    local floor = lib_characters.GetCurrentFloorSelf() % 100
    return episode * 10000 + map * 100 + floor
end

-- マップ番号 -> エリア名 (Room Timer と同じ表)
local mapNames = {
    [0]  = "Pioneer II",   [1]  = "Forest 1",    [2]  = "Forest 2",
    [3]  = "Cave 1",       [4]  = "Cave 2",      [5]  = "Cave 3",
    [6]  = "Mine 1",       [7]  = "Mine 2",      [8]  = "Ruins 1",
    [9]  = "Ruins 2",      [10] = "Ruins 3",     [11] = "Under the Dome",
    [12] = "Underground Channel",                [13] = "Control Room",
    [14] = "????",         [15] = "Lobby",       [16] = "BA Spaceship",
    [17] = "BA Temple",    [18] = "Lab",         [19] = "Temple Alpha",
    [20] = "Temple Beta 2",[21] = "Spaceship Alpha", [22] = "Spaceship Beta",
    [23] = "CCA",          [24] = "Jungle North",[25] = "Jungle East",
    [26] = "Mountain",     [27] = "Seaside",     [28] = "Seabed Upper",
    [29] = "Seabed Lower", [30] = "Cliffs of Gal Da Val",
    [31] = "Test Subject Disposal Area",         [32] = "Temple Final",
    [33] = "Spaceship Final",                    [34] = "Seaside at Night",
    [35] = "Control Tower",[36] = "Crater East", [37] = "Crater West",
    [38] = "Crater South", [39] = "Crater North",[40] = "Crater Interior",
    [41] = "Desert 1",     [42] = "Desert 2",    [43] = "Desert 3",
    [44] = "Meteor Impact Site",                 [45] = "Pioneer II",
}

local function areaName(area)
    local map = math.floor(area / 100) % 100
    return mapNames[map] or string.format("Map %d", map)
end

-- 自分が今いる部屋 (Room ID)。部屋の境目では 2 つの部屋にいることになる。ゲームに入っていなければ nil
local function currentRooms()
    local me = lib_characters.GetSelf()
    if me == 0 then return nil end
    return pso.read_u16(me + _PlayerRoom1), pso.read_u16(me + _PlayerRoom2)
end

-- ---------------------------------------------------------------------------
-- 投影 (Monster Reader の Wave Marker と同じ方式)
-- ---------------------------------------------------------------------------
local function v3(x, y, z) return { x = x, y = y, z = z } end
local function vdot(a, b) return a.x * b.x + a.y * b.y + a.z * b.z end
local function vcross(a, b)
    return v3(a.y * b.z - a.z * b.y,
              a.z * b.x - a.x * b.z,
              a.x * b.y - a.y * b.x)
end
local function vnorm(a)
    local l = math.sqrt(vdot(a, a))
    if l == 0 then return v3(0, 0, 0) end
    return v3(a.x / l, a.y / l, a.z / l)
end
local function clamp(x, lower, upper)
    return math.max(lower, math.min(upper, x))
end

local eyeWorld, eyeDir
local eyeRight, eyeUp    -- 画面の右 / 上に対応する単位ベクトル
local determinantScr
local resW, resH
local lastZoom = nil

local function updateProjection()
    resW = lib_helpers.GetResolutionWidth()
    resH = lib_helpers.GetResolutionHeight()
    local zoom = pso.read_u32(_CameraZoomLevel)
    if determinantScr == nil or zoom ~= lastZoom then
        lastZoom = zoom
        local aspect = resW / resH
        local fov = math.rad(
            math.deg(2 * math.atan(0.56470588 * aspect))
            - (zoom - 1) * 0.600 - clamp(zoom, 0, 1) * 0.300)
        determinantScr = aspect * 3 * resH / (6 * math.tan(0.5 * fov))
    end
    eyeWorld = v3(pso.read_f32(_CameraPosX), pso.read_f32(_CameraPosY), pso.read_f32(_CameraPosZ))
    eyeDir   = vnorm(v3(pso.read_f32(_CameraDirX), pso.read_f32(_CameraDirY), pso.read_f32(_CameraDirZ)))

    -- cross(eyeDir, 上) の長さは cos(ピッチ角) なので、正規化しないと
    -- 上空カメラのように真下に近い向きで投影が大きく縮む (issue #2)。
    -- 真下を向いていて向きが決まらないときは前回の right を使い回す。
    local right = vcross(eyeDir, v3(0, 1, 0))
    if vdot(right, right) > 0.00000001 or eyeRight == nil then
        eyeRight = vnorm(right)
    end
    eyeUp = vcross(eyeRight, eyeDir)
end

-- ワールド座標 -> 画面中心基準のオフセット。第3戻り値は前方=1 / 背面=-1
local function worldToScreen(px, py, pz)
    local rel = v3(px - eyeWorld.x, py - eyeWorld.y, pz - eyeWorld.z)
    local depth = vdot(eyeDir, rel)
    if depth == 0 then return 0, 0, -1 end
    local d = determinantScr / depth
    local vis = (depth > 0.0000001) and 1 or -1
    return vdot(eyeRight, rel) * d, -vdot(eyeUp, rel) * d, vis
end

-- worldToScreen の逆変換。画面中心基準のオフセット (sx, sy) を通る視線と
-- 高さ groundY の水平面との交点を返す。地面に当たらなければ nil。
local function screenToGround(sx, sy, groundY)
    local k = determinantScr
    local ray = v3(eyeDir.x * k + eyeRight.x * sx - eyeUp.x * sy,
                   eyeDir.y * k + eyeRight.y * sx - eyeUp.y * sy,
                   eyeDir.z * k + eyeRight.z * sx - eyeUp.z * sy)
    if math.abs(ray.y) < 0.000001 then return nil end
    local t = (groundY - eyeWorld.y) / ray.y
    if t <= 0 then return nil end
    return v3(eyeWorld.x + ray.x * t, groundY, eyeWorld.z + ray.z * t)
end

-- ---------------------------------------------------------------------------
-- ピンの色
-- 既定は部屋に入った順番 (スロット) の色で、ゲームの 1P 赤・2P 緑・3P 黄・4P 青に合わせる。
-- 自分の色は自分のアドオンが決めてサーバーへ送り、全員の画面で同じ色にする。
-- 色は 0xRRGGBB で持ち、描くときに ImU32 (0xAABBGGRR) にする
-- ---------------------------------------------------------------------------
local slotColors = {
    0xFF5050, -- 1P red
    0x50E650, -- 2P green
    0xFFE63C, -- 3P yellow
    0x50A0FF, -- 4P blue
}

-- 色を送ってこない古い版の人で、同じ部屋にもいない人の色 (名前から決める)
local palette = {
    0xFFC84A, -- amber
    0x4DB8FF, -- sky blue
    0x6BE36B, -- green
    0xFF8CE0, -- pink
    0xFF6B5A, -- red
    0x66E0FF, -- cyan
    0xFFA64D, -- orange
    0xB38CFF, -- violet
}

local slotByName = {}   -- 同じ部屋にいる人の名前 -> スロット番号 (0 始まり)

-- ロビーは 12 人まで入るので 4 色を繰り返す
local function slotColor(slot)
    return slotColors[slot % #slotColors + 1]
end

local function nameColor(name)
    local h = 0
    for i = 1, #name do
        h = (h * 31 + name:byte(i)) % 65536
    end
    return palette[(h % #palette) + 1]
end

-- 自分のピンの色。ゲームに入っていなければ nil
local function myColor()
    if options.colorMode == 2 then return options.customColor end
    if lib_characters.GetSelf() == 0 then return nil end
    return slotColor(pso.read_u32(_PlayerMyIndex))
end

-- 自分の矢印の色。ピンと同じ色にするなら false、ゲームに入っていなければ nil
local function myArrowColor()
    if options.arrowColorMode == 0 then return false end
    if options.arrowColorMode == 2 then return options.arrowCustomColor end
    if lib_characters.GetSelf() == 0 then return nil end
    return slotColor(pso.read_u32(_PlayerMyIndex))
end

local function updateSlots()
    slotByName = {}
    for _, pl in ipairs(lib_characters.GetPlayerList()) do
        local name = lib_characters.GetPlayerName(pl.address)
        if name ~= nil and name ~= "" then
            slotByName[name] = pl.index - 1
        end
    end
end

local function rgbToFloats(rgb)
    return math.floor(rgb / 65536) % 256 / 255,
           math.floor(rgb / 256) % 256 / 255,
           rgb % 256 / 255
end

local function rgbToImU32(rgb)
    local r, g, b = math.floor(rgb / 65536) % 256, math.floor(rgb / 256) % 256, rgb % 256
    return 0xFF000000 + b * 65536 + g * 256 + r
end

-- ピンの色 (0xRRGGBB)。持ち主が送ってきた色、無ければ同じ部屋でのスロットの色、それも無ければ名前から
local function pinColor(p)
    if p.color ~= nil then return p.color end
    local slot = slotByName[p.owner]
    if slot ~= nil then return slotColor(slot) end
    return nameColor(p.owner)
end

-- ---------------------------------------------------------------------------
-- 表示の絞り込み (options.pinFilter)。ピンも矢印も同じ扱い。
-- 「部屋」はエリア内の Room ID で、ピン・矢印には置いた人がその時いた部屋が付いている
-- ---------------------------------------------------------------------------
local filterChoices = { { 0, "All" }, { 1, "This room" }, { 2, "Mine" } }

-- 絞り込みの判定に使う自分の状態 { name, area, room1, room2 }。フレームごとに 1 回作る
local function filterView()
    local me = lib_characters.GetSelf()
    local room1, room2 = currentRooms()
    return { name = (me ~= 0) and lib_characters.GetPlayerName(me) or nil,
             area = currentArea(), room1 = room1, room2 = room2 }
end

-- 同じエリアの、自分が今いる部屋に置かれたものか。部屋を送ってこない古い版で置かれたものは違うとみなす
local function inMyRoom(item, view)
    return item.area == view.area and item.room ~= nil and view.room1 ~= nil
        and (item.room == view.room1 or item.room == view.room2)
end

-- id -> untilTick。クリックで消したピン・矢印のサーバー反映待ち。その間は消えたものとして見せない
-- (ピンと矢印の ID はサーバーが通しで振るので 1 つの表で足りる)
local pendingRemoves = {}

local function isShown(item, view)
    if pendingRemoves[item.id] ~= nil then return false end
    if options.pinFilter == 2 then return item.owner == view.name end
    if options.pinFilter == 1 then return inMyRoom(item, view) end
    return true
end

-- ---------------------------------------------------------------------------
-- 中継クライアントとのやり取り
-- ---------------------------------------------------------------------------
local relay = {
    session = nil,       -- クライアントの起動ごとに変わる ID
    time = 0,            -- クライアントが in.txt を書いた時刻 (unix 秒)
    ack = 0,             -- クライアントが処理済みの命令 seq
    status = "",
    message = "",
    channel = "",
    members = {},
    pinSet = nil,        -- 表示中のピンセットの名前 (サイトで選んだもの)。無ければ nil
}
local pins = {}          -- { id, owner, area, room, x, y, z, remaining, label }
local pinFirstSeen = {}  -- id -> 初めて見た tick。出現時のアニメーション用
local pendingMoves = {}  -- id -> { x, y, z, untilTick }。ドラッグで動かしたピンのサーバー反映待ち。その間は元の位置に戻して見せない
-- { id, owner, area, room, x1, y1, z1, x2, y2, z2, xm, ym, zm, remaining, color }。
-- (x1, y1, z1) が根元、(x2, y2, z2) が先端、(xm, ym, zm) は曲げたときに通る真ん中の点
local arrows = {}
local pendingArrowMoves = {}  -- id -> { x1, y1, z1, x2, y2, z2, xm, ym, zm, untilTick }。pendingMoves の矢印版
local moveFailed = false -- 反映待ちが時間切れになった。次のフレームで画面に知らせる
local removeFailed = false  -- moveFailed のクリックで消したとき版
local firstRead = true

local seq = os.time() * 1000   -- アドオン再読み込み後も単調増加させる
local lastSeqWritten = 0
local sentName = nil
local sentSession = nil
local sentColor = nil
local sentArrowColor = nil   -- 送った矢印の色。false = ピンと同じ色、nil = まだ送っていない
local lastReadTick = 0
local readInterval = 200

local function splitTabs(line)
    local fields = {}
    for field in (line .. "\t"):gmatch("([^\t]*)\t") do
        fields[#fields + 1] = field
    end
    return fields
end

local function cleanField(s)
    return (tostring(s):gsub("[%c]", " "))
end

local function formatNumber(n)
    return string.format("%.3f", n)
end

local function sendCommand(fields)
    seq = seq + 1
    -- クライアントが全部読み終えていればファイルを空にしてから書く (肥大化防止)
    local mode = (relay.ack >= lastSeqWritten) and "w" or "a"
    local file = io.open(outFileName, mode)
    if file == nil then return false end
    local parts = { string.format("%.0f", seq) }
    for _, f in ipairs(fields) do
        parts[#parts + 1] = cleanField(f)
    end
    file:write(table.concat(parts, "\t") .. "\n")
    file:close()
    lastSeqWritten = seq
    return true
end

local function relayAlive()
    return relay.session ~= nil and os.time() - relay.time <= 5
end

local function readInbox()
    local file = io.open(inFileName, "r")
    if file == nil then return end
    local text = file:read("*a")
    file:close()
    -- 置き換え方式なので基本的に書きかけは無いが、念のため終端行を確認する
    if text == nil or not text:find("\nend\r?\n?$") then return end

    local newRelay = { members = {} }
    local newPins = {}
    local newArrows = {}
    for line in text:gmatch("[^\n]+") do
        local f = splitTabs((line:gsub("\r$", "")))
        local kind = f[1]
        if kind == "session" then
            newRelay.session = f[2]
        elseif kind == "time" then
            newRelay.time = tonumber(f[2]) or 0
        elseif kind == "ack" then
            newRelay.ack = tonumber(f[2]) or 0
        elseif kind == "status" then
            newRelay.status = f[2] or ""
            newRelay.message = f[3] or ""
        elseif kind == "channel" then
            newRelay.channel = f[2] or ""
        elseif kind == "pinset" then
            newRelay.pinSet = f[2]
        elseif kind == "member" then
            table.insert(newRelay.members, f[2] or "")
        elseif kind == "pin" and #f >= 8 then
            local p = {
                id = tonumber(f[2]),
                owner = f[3],
                area = tonumber(f[4]),   -- currentArea() の値 (旧版のアドオンからはフロア番号)
                x = tonumber(f[5]),
                y = tonumber(f[6]),
                z = tonumber(f[7]),
                remaining = tonumber(f[8]) or -1,
                label = f[9] or "",
                orderAll = tonumber(f[10]),     -- サーバーが振った全員通しの番号
                orderOwner = tonumber(f[11]),   -- サーバーが振った持ち主ごとの番号
                color = tonumber(f[12] or "", 16),  -- 持ち主が選んだ色 (0xRRGGBB)。古い版なら nil
                orderRoom = tonumber(f[13] or ""),  -- サーバーが振った部屋ごとの番号
                room = tonumber(f[14] or ""),       -- 置いた人がいた部屋 (Room ID)。古い版なら nil
                locked = f[15] == "1",              -- ピンセットのピン: 動かせない・消せない
            }
            if p.id and p.area and p.x and p.y and p.z then
                table.insert(newPins, p)
            end
        elseif kind == "arrow" and #f >= 12 then
            local a = {
                id = tonumber(f[2]),
                owner = f[3],
                area = tonumber(f[4]),
                x1 = tonumber(f[5]), y1 = tonumber(f[6]), z1 = tonumber(f[7]),
                x2 = tonumber(f[8]), y2 = tonumber(f[9]), z2 = tonumber(f[10]),
                remaining = tonumber(f[11]) or -1,
                color = tonumber(f[12] or "", 16),
                -- 曲げたときに通る真ん中の点。曲げに対応していないサーバー・クライアントなら両端の中点
                xm = tonumber(f[13] or ""), ym = tonumber(f[14] or ""), zm = tonumber(f[15] or ""),
                room = tonumber(f[16] or ""),   -- 引いた人がいた部屋 (Room ID)。古い版なら nil
                locked = f[17] == "1",          -- ピンセットの矢印: 動かせない・消せない
            }
            if a.id and a.area and a.x1 and a.y1 and a.z1 and a.x2 and a.y2 and a.z2 then
                if not (a.xm and a.ym and a.zm) then
                    a.xm, a.ym, a.zm = (a.x1 + a.x2) * 0.5, (a.y1 + a.y2) * 0.5, (a.z1 + a.z2) * 0.5
                end
                table.insert(newArrows, a)
            end
        end
    end

    relay.session = newRelay.session
    relay.time = newRelay.time or 0
    relay.ack = newRelay.ack or 0
    relay.status = newRelay.status or ""
    relay.message = newRelay.message or ""
    relay.channel = newRelay.channel or ""
    relay.members = newRelay.members
    relay.pinSet = newRelay.pinSet
    pins = newPins
    arrows = newArrows

    -- 動かしたピンがサーバーから新しい位置で届いたら反映待ちを終える。
    -- 待ちきれなければ元の位置に戻し、反映されなかったことを知らせる
    local nowTick = pso.get_tick_count()
    local function settle(pending, items, arrived)
        local byId = {}
        for _, p in ipairs(items) do byId[p.id] = p end
        for id, m in pairs(pending) do
            local p = byId[id]
            if p == nil or arrived(p, m) then
                pending[id] = nil
            elseif nowTick > m.untilTick then
                pending[id] = nil
                moveFailed = true
            end
        end
    end
    local function near(a, b) return math.abs(a - b) < 0.01 end
    settle(pendingMoves, pins, function(p, m)
        return near(p.x, m.x) and near(p.z, m.z)
    end)
    settle(pendingArrowMoves, arrows, function(a, m)
        return near(a.x1, m.x1) and near(a.z1, m.z1) and near(a.x2, m.x2) and near(a.z2, m.z2)
            and near(a.xm, m.xm) and near(a.zm, m.zm)
    end)

    -- クリックで消したピン・矢印が一覧から無くなったら反映待ちを終える。
    -- 待ちきれなければまた見せて、消せなかったことを知らせる
    local alive = {}
    for _, p in ipairs(pins) do alive[p.id] = true end
    for _, a in ipairs(arrows) do alive[a.id] = true end
    for id, untilTick in pairs(pendingRemoves) do
        if not alive[id] then
            pendingRemoves[id] = nil
        elseif nowTick > untilTick then
            pendingRemoves[id] = nil
            removeFailed = true
        end
    end

    -- 置いた順番の番号はサーバーが立てるたびに増やして振る (ピンが消えても戻らない)。
    -- 番号を送ってこない古いサーバー・クライアントの場合は、今あるピンを ID 順に数える。
    -- orderOwner は持ち主ごと、orderAll は全員通し、orderRoom は部屋ごとの番号。
    -- 部屋ごとの番号を送ってこない古いサーバー・クライアントの場合は全員通しの番号で代える
    local sorted = {}
    for i, p in ipairs(pins) do sorted[i] = p end
    table.sort(sorted, function(a, b) return a.id < b.id end)
    local perOwner = {}
    for i, p in ipairs(sorted) do
        perOwner[p.owner] = (perOwner[p.owner] or 0) + 1
        p.orderOwner = p.orderOwner or perOwner[p.owner]
        p.orderAll = p.orderAll or i
        p.orderRoom = p.orderRoom or p.orderAll
    end

    local now = pso.get_tick_count()
    local seen = {}
    for _, p in ipairs(pins) do
        seen[p.id] = true
        if pinFirstSeen[p.id] == nil then
            -- 起動直後に既にあったピンはアニメーションさせない
            pinFirstSeen[p.id] = firstRead and 0 or now
        end
    end
    for id, _ in pairs(pinFirstSeen) do
        if not seen[id] then pinFirstSeen[id] = nil end
    end
    firstRead = false
end

-- プレイヤー名をクライアントへ伝える。名前が変わった時とクライアントが再起動した時に送り直す
local function syncName()
    if not relayAlive() then return end
    local me = lib_characters.GetSelf()
    if me == 0 then return end
    local name = lib_characters.GetPlayerName(me)
    if name == nil or name == "" then return end
    if name ~= sentName or relay.session ~= sentSession then
        if sendCommand({ "name", name }) then
            sentName = name
            sentSession = relay.session
            sentColor = nil   -- サーバーは色を名前ごとに持つので送り直す
            sentArrowColor = nil
        end
    end
end

-- 自分のピン・矢印の色をクライアントへ伝える。部屋を移ってスロットが変わった時や設定を変えた時に送り直す
local function syncColor()
    if not relayAlive() or sentName == nil then return end
    local color = myColor()
    if color ~= nil and color ~= sentColor then
        if sendCommand({ "color", string.format("%06X", color) }) then
            sentColor = color
        end
    end
    -- 矢印の色。ピンと同じ色にするときは空で送り、サーバーにピンの色を使わせる
    local arrowColor = myArrowColor()
    if arrowColor ~= nil and arrowColor ~= sentArrowColor then
        local value = arrowColor and string.format("%06X", arrowColor) or ""
        if sendCommand({ "arrow_color", value }) then
            sentArrowColor = arrowColor
        end
    end
end

-- ---------------------------------------------------------------------------
-- ピン操作
-- ---------------------------------------------------------------------------
local notice = nil        -- 画面に一時表示するメッセージ
local noticeUntil = 0
local lastMouseClick = nil   -- { tick, result }。設定画面の診断表示用

local function showNotice(text)
    notice = text
    noticeUntil = pso.get_tick_count() + 2500
end

-- ピンを立てられない場合は false と理由を返す (理由は画面にも一時表示する)
local function canPlacePin()
    if not options.enable then return false, "addon is disabled" end
    if lib_menu.IsMenuUnavailable() then return false, "game is loading" end
    if not relayAlive() then
        showNotice("Pin Share: Rappy Runs Client is not relaying")
        return false, "Rappy Runs Client is not relaying"
    end
    if relay.status ~= "connected" then
        showNotice("Pin Share: not connected to server")
        return false, "not connected to server"
    end
    return true
end

local function placePin(x, y, z)
    syncName()
    syncColor()
    -- 部屋は立てた人が今いる部屋にする (カーソルで隣の部屋を指していても)
    sendCommand({ "add", currentArea(),
        formatNumber(x), formatNumber(y), formatNumber(z), options.ttl, options.label, options.maxPins,
        currentRooms() or "" })
end

local function placePinAtFeet()
    if not canPlacePin() then return end
    local me = lib_characters.GetSelf()
    if me == 0 then return end
    local pos = lib_characters.GetPlayerCoordinates(me)
    placePin(pos.x, pos.y, pos.z)
end

-- キャラの向いている方向へ frontDist 先にピン (カーソルが無いコントローラー向け)。
-- 高さは地面を読めないので足元と同じにする
local function placePinInFront()
    if not canPlacePin() then return end
    local me = lib_characters.GetSelf()
    if me == 0 then return end
    local pos = lib_characters.GetPlayerCoordinates(me)
    local angle = pso.read_u16(me + _PlayerFacing) * (2 * math.pi / 65536)
    placePin(pos.x + math.sin(angle) * options.frontDist, pos.y, pos.z + math.cos(angle) * options.frontDist)
end

-- 自分から遠すぎる位置は、自分からの方向を保ったまま距離を詰める
local function clampReach(hit, myPos)
    local dx, dz = hit.x - myPos.x, hit.z - myPos.z
    local dist = math.sqrt(dx * dx + dz * dz)
    if options.maxCursorDist > 0 and dist > options.maxCursorDist then
        local s = options.maxCursorDist / dist
        hit.x = myPos.x + dx * s
        hit.z = myPos.z + dz * s
    end
end

-- 結果を文字列で返す (設定画面の診断表示用)
local function placePinAtCursor()
    local ok, reason = canPlacePin()
    if not ok then return reason end
    local me = lib_characters.GetSelf()
    if me == 0 then return "player not found" end
    local pos = lib_characters.GetPlayerCoordinates(me)

    updateProjection()
    local mx, my = imgui.GetMousePos()
    local hit = screenToGround(mx - resW * 0.5, my - resH * 0.5, pos.y)
    if hit == nil then
        showNotice("Pin Share: cursor is not pointing at the ground")
        return string.format("cursor is not pointing at the ground (mouse %d,%d)", math.floor(mx), math.floor(my))
    end
    clampReach(hit, pos)
    placePin(hit.x, hit.y, hit.z)
    return "pin placed"
end

-- ---------------------------------------------------------------------------
-- 矢印を引く (arrowKey を押しながら mouseButton でドラッグ。押した所が根元、離した所が先端)
-- 高さは地面の高さを読めないので、ピンと同じく自分の足元の高さにする
-- ---------------------------------------------------------------------------
local arrowDraft = nil     -- 引いている途中の矢印 { area, button, x1, y1, z1, x2, y2, z2, xm, ym, zm }
local minArrowLength = 15  -- これより短い矢印は引かない (ゲーム内単位)

-- キーが押されているか。GetAsyncKeyState が使えればそれで、無ければ ImGui に聞く
local getAsyncKeyState = nil
do
    local ok, ffi = pcall(require, "ffi")
    if ok then
        pcall(ffi.cdef, "short __stdcall GetAsyncKeyState(int vKey);")
        local found, fn = pcall(function() return ffi.C.GetAsyncKeyState end)
        if found then getAsyncKeyState = fn end
    end
end

local function isKeyDown(vk)
    if vk == 0 then return false end
    if getAsyncKeyState ~= nil then
        return bit.band(getAsyncKeyState(vk), 0x8000) ~= 0
    end
    return imgui.IsKeyDown(vk)
end

-- ---------------------------------------------------------------------------
-- 入力の優先 (pinshare-input.dll)
-- アドオンからはキーを「使った」とゲームに伝えられないため、F6 などを押すとゲームの機能も動いてしまう。
-- Rappy Runs Client がこのフォルダに置く pinshare-input.dll を読み込み、割り当て中のキーと
-- コントローラーのボタンだけをゲームから隠してもらう (割り当てていないキーはそのままゲームに届く)。
-- アドオンは入力を今までどおり受け取る。コントローラーの押下はアドオンに届かないので DLL に問い合わせる。
-- 毎フレームの inputLib.pinshare_input_poll() が止まると (アドオンを止めた・エラーで止まった)
-- DLL は 2 秒で隠すのをやめる。DLL が無い (古いクライアント) ときは今までどおり動く。
-- ---------------------------------------------------------------------------
local inputFfi = nil
local inputLib = nil
local inputLoadError = nil
do
    local ok, ffi = pcall(require, "ffi")
    if ok then
        pcall(ffi.cdef, [[
            int pinshare_input_version(void);
            int pinshare_input_status(void);
            void pinshare_input_set_keys(const int* vks, int n);
            void pinshare_input_set_pad(const unsigned* mains, const unsigned* mods, int n);
            unsigned pinshare_input_poll(void);
            unsigned pinshare_input_pad_raw(void);
        ]])
        local loaded, lib = pcall(ffi.load, "addons\\Pin Share\\pinshare-input.dll")
        if loaded then
            inputFfi, inputLib = ffi, lib
        else
            inputLoadError = tostring(lib)
        end
    end
end

local padActionLabels = {
    padFeet = "Pin at feet:", padClear = "Clear mine:", padFilter = "Cycle filter:", padFront = "Pin in front:",
}

local padButtonNames = {
    { 0x0001, "Up" }, { 0x0002, "Down" }, { 0x0004, "Left" }, { 0x0008, "Right" },
    { 0x0010, "Start" }, { 0x0020, "Back" }, { 0x0040, "L3" }, { 0x0080, "R3" },
    { 0x0100, "LB" }, { 0x0200, "RB" }, { 0x1000, "A" }, { 0x2000, "B" },
    { 0x4000, "X" }, { 0x8000, "Y" }, { 0x10000, "LT" }, { 0x20000, "RT" },
}

local function padButtonName(bits)
    for _, b in ipairs(padButtonNames) do
        if b[1] == bits then return b[2] end
    end
    return string.format("0x%X", bits)
end

local padCapture = nil      -- { key, order = {押した順のビット}, seen } コントローラー割り当て待ち

local function setPadCapture(capture)
    padCapture = capture
    inputDirty = true
end

-- 割り当て中のキーとボタンを DLL に渡す。毎フレーム呼んでよい (変化が無ければ何もしない)
local function syncInputBindings()
    if inputLib == nil or not inputDirty then return end
    inputDirty = false
    local keys, mains, mods = {}, {}, {}
    if options.enable then
        -- 押しながら使う arrowKey / deleteKey は隠さない (Shift などをゲームから奪わない)
        for _, name in ipairs({ "cursorKey", "feetKey", "clearKey", "filterKey", "frontKey" }) do
            if options[name] ~= 0 then table.insert(keys, options[name]) end
        end
        -- 割り当て待ちの間は、既存の割り当てが反応しないよう全部外す
        if padCapture == nil then
            for i, key in ipairs(padActionKeys) do
                mains[i] = options[key]
                mods[i] = options[key .. "Mod"]
            end
        end
    end
    inputLib.pinshare_input_set_keys(#keys > 0 and inputFfi.new("int[?]", #keys, keys) or nil, #keys)
    inputLib.pinshare_input_set_pad(#mains > 0 and inputFfi.new("unsigned[?]", #mains, mains) or nil,
        #mods > 0 and inputFfi.new("unsigned[?]", #mods, mods) or nil, #mains)
end

-- 割り当て待ちのコントローラー入力を見る。全部離したら、最初に押したボタンを同時押し、
-- 最後に押したボタンを本体として保存する (1 つだけなら単独)
local function updatePadCapture()
    if padCapture == nil or inputLib == nil then return end
    local raw = inputLib.pinshare_input_pad_raw()
    if raw ~= 0 then
        for _, b in ipairs(padButtonNames) do
            if bit.band(raw, b[1]) ~= 0 and bit.band(padCapture.seen, b[1]) == 0 then
                padCapture.seen = bit.bor(padCapture.seen, b[1])
                table.insert(padCapture.order, b[1])
            end
        end
    elseif #padCapture.order > 0 then
        local order = padCapture.order
        options[padCapture.key] = order[#order]
        options[padCapture.key .. "Mod"] = (#order >= 2) and order[1] or 0
        setPadCapture(nil)
        SaveOptions()
    end
end

-- カーソルの位置から矢印を引き始める。結果を文字列で返す (設定画面の診断表示用)
local function startArrowAtCursor(button)
    local ok, reason = canPlacePin()
    if not ok then return reason end
    local me = lib_characters.GetSelf()
    if me == 0 then return "player not found" end
    local pos = lib_characters.GetPlayerCoordinates(me)

    updateProjection()
    local mx, my = imgui.GetMousePos()
    local hit = screenToGround(mx - resW * 0.5, my - resH * 0.5, pos.y)
    if hit == nil then
        showNotice("Pin Share: cursor is not pointing at the ground")
        return "cursor is not pointing at the ground"
    end
    clampReach(hit, pos)
    arrowDraft = { area = currentArea(), room = currentRooms(), button = button,
                   x1 = hit.x, y1 = hit.y, z1 = hit.z, x2 = hit.x, y2 = hit.y, z2 = hit.z }
    return "drawing arrow"
end

-- 引いている矢印の先端をカーソルに合わせる。updateProjection() の後に呼ぶ
local function updateArrowDraft(myPos)
    if arrowDraft == nil then return end
    local mx, my = imgui.GetMousePos()
    local hit = screenToGround(mx - resW * 0.5, my - resH * 0.5, arrowDraft.y1)
    if hit == nil then return end
    clampReach(hit, myPos)
    local d = arrowDraft
    d.x2, d.y2, d.z2 = hit.x, hit.y, hit.z
    -- 引いた直後はまっすぐ。真ん中の点は後からドラッグして曲げる
    d.xm, d.ym, d.zm = (d.x1 + d.x2) * 0.5, (d.y1 + d.y2) * 0.5, (d.z1 + d.z2) * 0.5
end

-- 矢印の根元・先端 (withMid なら真ん中の点も) を命令の引数にして fields の後ろに足す
local function appendArrowPoints(fields, g, withMid)
    local keys = { "x1", "y1", "z1", "x2", "y2", "z2" }
    if withMid then
        keys[#keys + 1], keys[#keys + 2], keys[#keys + 3] = "xm", "ym", "zm"
    end
    for _, k in ipairs(keys) do
        fields[#fields + 1] = formatNumber(g[k])
    end
    return fields
end

-- ボタンを離したら矢印を置く
local function finishArrow()
    if arrowDraft == nil or imgui.IsMouseDown(arrowDraft.button) then return end
    local a = arrowDraft
    arrowDraft = nil
    local dx, dz = a.x2 - a.x1, a.z2 - a.z1
    if dx * dx + dz * dz < minArrowLength * minArrowLength then
        showNotice("Pin Share: drag farther to draw an arrow")
        return
    end
    if not canPlacePin() then return end
    syncName()
    syncColor()
    local fields = appendArrowPoints({ "arrow_add", a.area }, a, false)
    fields[#fields + 1] = options.ttl
    fields[#fields + 1] = options.maxPins
    -- 部屋はこの後ろに付けるので、真ん中の点 (引いた直後は両端の中点) も送る
    fields[#fields + 1] = formatNumber(a.xm)
    fields[#fields + 1] = formatNumber(a.ym)
    fields[#fields + 1] = formatNumber(a.zm)
    fields[#fields + 1] = a.room or ""
    sendCommand(fields)
end

local function clearMyPins()
    if not canPlacePin() then return end
    syncName()
    sendCommand({ "clear_mine" })
end

-- ---------------------------------------------------------------------------
-- ドラッグでピンを動かす (左ボタンで頭の円を掴む)。他の人のピンは設定で許したときだけ
-- 矢印も同じく、根元の点か先端の三角を掴んで動かす (動かした側だけが動くので、伸び縮み・向きが変わる)。
-- 矢印の真ん中の点を掴んで引っ張ると、その点を通るカーブに曲がる
-- ---------------------------------------------------------------------------
local drag = nil          -- { kind = "pin" | "arrow", id, point (矢印: 1 = 根元, 2 = 先端, 3 = 真ん中), offX, offY, startX, startY, groundY, moved, x, y, z }
local grabTargets = {}    -- 今のフレームで掴めるもの { kind, item, point, cx, cy, r, tipX, tipY }
local dragThreshold = 4   -- これ以上動かしたらドラッグとみなす (ピクセル)
local pendingMoveMs = 3000
-- 今のフレームでクリックして消せるもの { kind, item, circles = { { x, y, r }, ... }, segs = { { ax, ay, bx, by }, ... } }。
-- 掴む所より広く、ピンは頭と軸、矢印は線のどこを指してもよい
local deleteTargets = {}
local deleteSegRadius = 7   -- segs の線からこの距離までを指したとみなす (ピクセル)

local function dragging(kind, id)
    return drag ~= nil and drag.kind == kind and drag.id == id and drag.x ~= nil
end

-- 描画に使うピンの位置。ドラッグ中・反映待ちならその位置
local function pinPosition(p)
    if dragging("pin", p.id) then
        return drag.x, drag.y, drag.z
    end
    local m = pendingMoves[p.id]
    if m ~= nil then
        return m.x, m.y, m.z
    end
    return p.x, p.y, p.z
end

-- 描画に使う矢印の根元・先端・真ん中の点 { x1, y1, z1, x2, y2, z2, xm, ym, zm }。
-- 反映待ちならその位置で、ドラッグ中なら掴んだ所をその位置にする。
-- 根元・先端を動かしたときは、動いた分の半分だけ真ん中の点も動かして曲がり具合を保つ
local function arrowPosition(a)
    local m = pendingArrowMoves[a.id] or a
    local g = { x1 = m.x1, y1 = m.y1, z1 = m.z1, x2 = m.x2, y2 = m.y2, z2 = m.z2,
                xm = m.xm, ym = m.ym, zm = m.zm }
    if dragging("arrow", a.id) then
        if drag.point == 3 then
            g.xm, g.ym, g.zm = drag.x, drag.y, drag.z
        else
            local s = tostring(drag.point)
            local dx, dy, dz = drag.x - g["x" .. s], drag.y - g["y" .. s], drag.z - g["z" .. s]
            g["x" .. s], g["y" .. s], g["z" .. s] = drag.x, drag.y, drag.z
            g.xm, g.ym, g.zm = g.xm + dx * 0.5, g.ym + dy * 0.5, g.zm + dz * 0.5
        end
    end
    return g
end

-- ドラッグ中のピン・矢印の端の位置をカーソルに合わせる。高さは掴んだもののまま
-- updateProjection() の後に呼ぶ
local function updateDragPosition(myPos)
    if drag == nil then return end
    local mx, my = imgui.GetMousePos()
    if not drag.moved then
        local dx, dy = mx - drag.startX, my - drag.startY
        if dx * dx + dy * dy < dragThreshold * dragThreshold then return end
        drag.moved = true
    end
    -- 掴んだ所 (ピンの頭など) が指している点からずれている分を足して地面を指す
    local hit = screenToGround(mx + drag.offX - resW * 0.5, my + drag.offY - resH * 0.5, drag.groundY)
    if hit == nil then return end
    clampReach(hit, myPos)
    drag.x, drag.y, drag.z = hit.x, hit.y, hit.z
end

-- ドラッグを終えて、動かした位置をサーバーへ送る。反映されるまではその位置で見せる
local function sendDragResult()
    local untilTick = pso.get_tick_count() + pendingMoveMs
    if drag.kind == "pin" then
        sendCommand({ "move", drag.id, formatNumber(drag.x), formatNumber(drag.y), formatNumber(drag.z) })
        pendingMoves[drag.id] = { x = drag.x, y = drag.y, z = drag.z, untilTick = untilTick }
        return
    end
    for _, a in ipairs(arrows) do
        if a.id == drag.id then
            local g = arrowPosition(a)
            sendCommand(appendArrowPoints({ "arrow_move", a.id }, g, true))
            g.untilTick = untilTick
            pendingArrowMoves[a.id] = g
            return
        end
    end
end

-- 点 (px, py) と線分 s の距離の 2 乗
local function distToSegmentSq(px, py, s)
    local vx, vy = s.bx - s.ax, s.by - s.ay
    local l = vx * vx + vy * vy
    local t = 0
    if l > 0 then t = clamp(((px - s.ax) * vx + (py - s.ay) * vy) / l, 0, 1) end
    local dx, dy = px - (s.ax + vx * t), py - (s.ay + vy * t)
    return dx * dx + dy * dy
end

local function hitsDeleteTarget(t, mx, my)
    for _, c in ipairs(t.circles) do
        local dx, dy = mx - c.x, my - c.y
        if dx * dx + dy * dy <= (c.r + 3) * (c.r + 3) then return true end
    end
    for _, s in ipairs(t.segs or {}) do
        if distToSegmentSq(mx, my, s) <= deleteSegRadius * deleteSegRadius then return true end
    end
    return false
end

-- カーソルが指しているピン・矢印を消す (deleteKey を押しながら mouseButton でクリック)。
-- 何も指していなければ nil、指していれば結果を文字列で返す (設定画面の診断表示用)。
-- drawPins() の後に呼ぶ (deleteTargets がそのフレームのものになる)
local function deleteAtCursor()
    local mx, my = imgui.GetMousePos()
    -- 後に描いた (手前に見える) ものを優先する
    for i = #deleteTargets, 1, -1 do
        local t = deleteTargets[i]
        if hitsDeleteTarget(t, mx, my) then
            local ok, reason = canPlacePin()
            if not ok then return reason end
            sendCommand({ t.kind == "pin" and "remove" or "arrow_remove", t.item.id })
            -- 反映されるまでの間にもう一度クリックして、ピンや矢印を置いてしまわないようにすぐ隠す
            pendingRemoves[t.item.id] = pso.get_tick_count() + pendingMoveMs
            if drag ~= nil and drag.id == t.item.id then drag = nil end
            return t.kind .. " deleted"
        end
    end
    return nil
end

-- 掴む・離すの処理。drawPins() の後に呼ぶ (grabTargets がそのフレームのものになる)
local function updateDrag()
    if moveFailed then
        moveFailed = false
        showNotice("Pin Share: move was not applied (update relay client / server)")
    end
    if removeFailed then
        removeFailed = false
        showNotice("Pin Share: delete was not applied")
    end

    if drag ~= nil then
        if not imgui.IsMouseDown(0) then
            if drag.moved and drag.x ~= nil and canPlacePin() then
                sendDragResult()
            end
            drag = nil
        end
        return
    end

    if imgui.IsMouseClicked(0) and pso.is_pso_focused() and not imgui.IsAnyItemHovered() then
        local mx, my = imgui.GetMousePos()
        -- 後に描いた (手前に見える) ものを優先する
        for i = #grabTargets, 1, -1 do
            local t = grabTargets[i]
            local dx, dy = mx - t.cx, my - t.cy
            if dx * dx + dy * dy <= (t.r + 3) * (t.r + 3) then
                drag = { kind = t.kind, id = t.item.id, point = t.point, offX = t.tipX - mx, offY = t.tipY - my,
                         startX = mx, startY = my, groundY = t.groundY, moved = false }
                break
            end
        end
    end
end

-- ---------------------------------------------------------------------------
-- 描画
-- ---------------------------------------------------------------------------
local function colorToFloats(c)
    return bit.band(c, 0xFF) / 255,
           bit.band(bit.rshift(c, 8), 0xFF) / 255,
           bit.band(bit.rshift(c, 16), 0xFF) / 255
end

local markerWindowParams = { "NoTitleBar", "NoResize", "NoMove", "NoInputs", "NoSavedSettings",
                             "NoScrollbar", "NoFocusOnAppearing" }
local pulseMs = 1500

-- 設定に応じたピンの番号 (表示しないなら nil)
-- 最大値が決めてあれば 0, 1, …, 最大値 を繰り返すように丸める
local function pinOrder(p)
    local order
    if options.orderMode == 1 then order = p.orderOwner end
    if options.orderMode == 2 then order = p.orderAll end
    if options.orderMode == 3 then order = p.orderRoom end
    if order ~= nil and options.orderMax > 0 then
        order = order % (options.orderMax + 1)
    end
    return order
end

-- ピン 1 本を描く。(ax, ay) は画面上の絶対座標で、画面内ならピンの先端、画面外なら端の位置。
-- dirX/dirY が非 nil なら画面外表示で、その方向に矢印を付ける。
-- 頭の円の中心と半径を返す (ドラッグで掴む判定用)
local function drawPin(p, ax, ay, text, color, dirX, dirY)
    local tw, th = imgui.CalcTextSize(text)
    tw, th = tw * options.fontScale, th * options.fontScale

    -- 頭の円の中に置いた順番の番号を出す。番号が収まるよう円の大きさを決める
    local order = pinOrder(p)
    local orderText = order and tostring(order) or nil
    local nw, nh = 0, 0
    local headR = 9
    if orderText ~= nil then
        nw, nh = imgui.CalcTextSize(orderText)
        nw, nh = nw * options.fontScale, nh * options.fontScale
        headR = math.max(9, math.max(nw, nh) * 0.5 + 3)
    end
    local pad = 30   -- 出現アニメーションの輪がはみ出さない余白
    local w = math.max(tw + 12, pad * 2)
    local iconTop = th + 6
    local h = iconTop + headR * 2 + 12 + pad

    -- 画面内: 先端が (ax, ay)。画面外: 頭の円の中心が (ax, ay)
    local tipOffset = iconTop + headR * 2 + 12
    local wx, wy
    if dirX == nil then
        wx, wy = ax - w * 0.5, ay - tipOffset
    else
        wx, wy = ax - w * 0.5, ay - (iconTop + headR)
    end

    imgui.SetNextWindowPos(wx, wy, "Always")
    imgui.SetNextWindowSize(w, h, "Always")
    imgui.PushStyleColor("WindowBg", 0, 0, 0, 0)
    imgui.PushStyleColor("Border", 0, 0, 0, 0)
    if imgui.Begin("PinShare##" .. p.id, nil, markerWindowParams) then
        imgui.SetWindowFontScale(options.fontScale)

        -- ラベル (影付き)
        local tx = wx + (w - tw) * 0.5
        local r, g, b = colorToFloats(color)
        imgui.SetCursorPos(tx - wx + 1, 4)
        imgui.TextColored(0, 0, 0, 0.9, text)
        imgui.SetCursorPos(tx - wx, 3)
        imgui.TextColored(r, g, b, 1, text)

        local cx, cy = wx + w * 0.5, wy + iconTop + headR
        local outline = 0xE0000000

        -- 頭の中身: 番号 (黒文字) か、番号なしなら白い点。図形より後に描いて上に重ねる
        local function drawHeadContent()
            if orderText ~= nil then
                imgui.SetCursorPos(cx - wx - nw * 0.5, cy - wy - nh * 0.5)
                imgui.TextColored(0, 0, 0, 1, orderText)
            else
                imgui.AddCircleFilled(cx, cy, 3.5, 0xFFFFFFFF, 12)
            end
        end

        if dirX == nil then
            -- 画面内: 逆三角の軸 + 円の頭
            local tipY = wy + tipOffset
            imgui.AddTriangleFilled(cx - 7, cy + 3, cx + 7, cy + 3, cx, tipY, outline)
            imgui.AddTriangleFilled(cx - 5, cy + 3, cx + 5, cy + 3, cx, tipY - 2, color)
            imgui.AddCircleFilled(cx, cy, headR + 1.5, outline, 20)
            imgui.AddCircleFilled(cx, cy, headR, color, 20)
            drawHeadContent()

            -- 出現アニメーション: 先端から広がる輪
            local born = pinFirstSeen[p.id] or 0
            local age = pso.get_tick_count() - born
            if born > 0 and age < pulseMs then
                local t = age / pulseMs
                local alpha = math.floor((1 - t) * 255)
                local ring = bit.bor(bit.lshift(alpha, 24), bit.band(color, 0x00FFFFFF))
                imgui.AddCircle(cx, tipY, 6 + t * (pad - 6), ring, 24, 2.5)
            end
        else
            -- 画面外: 円の頭 + 対象方向を指す三角
            local ox, oy = -dirY, dirX
            local bx, by = cx + dirX * (headR + 1), cy + dirY * (headR + 1)
            imgui.AddTriangleFilled(bx + ox * 7, by + oy * 7, bx - ox * 7, by - oy * 7,
                cx + dirX * (headR + 12), cy + dirY * (headR + 12), color)
            imgui.AddCircleFilled(cx, cy, headR + 1.5, outline, 20)
            imgui.AddCircleFilled(cx, cy, headR, color, 20)
            drawHeadContent()
        end
    end
    imgui.End()
    imgui.PopStyleColor(2)
    return wx + w * 0.5, wy + iconTop + headR, headR
end

-- 掴めるピンの頭に重ねる透明な窓。マウスが乗っている間は ImGui がマウスを取るので、
-- 掴むための左クリックがゲーム側に届かない
local grabWindowParams = { "NoTitleBar", "NoResize", "NoMove", "NoSavedSettings",
                           "NoScrollbar", "NoFocusOnAppearing" }

local function drawGrabArea(key, cx, cy, r)
    local s = r * 2 + 6
    imgui.SetNextWindowPos(cx - s * 0.5, cy - s * 0.5, "Always")
    imgui.SetNextWindowSize(s, s, "Always")
    imgui.PushStyleColor("WindowBg", 0, 0, 0, 0)
    imgui.PushStyleColor("Border", 0, 0, 0, 0)
    imgui.Begin("PinShareGrab##" .. key, nil, grabWindowParams)
    imgui.End()
    imgui.PopStyleColor(2)
end

-- 線分の両端を画面上の絶対座標にする。カメラの後ろにはみ出した側は、カメラの少し前で切る。
-- 全部カメラの後ろなら nil。第 5, 6 戻り値は根元 / 先端が切られずに見えているか
local nearDepth = 5
local function depth(x, y, z)
    return vdot(eyeDir, v3(x - eyeWorld.x, y - eyeWorld.y, z - eyeWorld.z))
end

local function projectSegment(x1, y1, z1, x2, y2, z2)
    local d1, d2 = depth(x1, y1, z1), depth(x2, y2, z2)
    if d1 < nearDepth and d2 < nearDepth then return nil end
    local keep1, keep2 = d1 >= nearDepth, d2 >= nearDepth
    if not keep1 then
        local t = (nearDepth - d1) / (d2 - d1)
        x1, y1, z1 = x1 + (x2 - x1) * t, y1 + (y2 - y1) * t, z1 + (z2 - z1) * t
    elseif not keep2 then
        local t = (nearDepth - d2) / (d1 - d2)
        x2, y2, z2 = x2 + (x1 - x2) * t, y2 + (y1 - y2) * t, z2 + (z1 - z2) * t
    end
    local sx1, sy1 = worldToScreen(x1, y1, z1)
    local sx2, sy2 = worldToScreen(x2, y2, z2)
    return resW * 0.5 + sx1, resH * 0.5 + sy1, resW * 0.5 + sx2, resH * 0.5 + sy2, keep1, keep2
end

-- 矢印の線は、根元・真ん中の点・先端を通る 2 次ベジェ曲線を細かい線分に分けて描く
local arrowSegments = 16

-- 矢印 1 本 (arrowPosition() の形) を今のウィンドウの描画リストに描く。showMid なら真ん中の点に印を付ける。
-- 見えている所の画面上の位置を返す (掴む判定用。見えていなければ nil):
--   { tail = { x, y }, head = { x, y, tipX, tipY } (三角の重心と先端), mid = { x, y },
--     line = { { ax, ay, bx, by }, ... } (線と三角の軸。クリックで消す判定用) }
local function drawArrowShape(g, color, showMid)
    local outline = 0xE0000000
    local result = {}

    -- 真ん中の点を t = 0.5 で通るように制御点を決める
    local cx = 2 * g.xm - (g.x1 + g.x2) * 0.5
    local cy = 2 * g.ym - (g.y1 + g.y2) * 0.5
    local cz = 2 * g.zm - (g.z1 + g.z2) * 0.5
    local function at(t)
        local a, b, c = (1 - t) * (1 - t), 2 * (1 - t) * t, t * t
        return a * g.x1 + b * cx + c * g.x2, a * g.y1 + b * cy + c * g.y2, a * g.z1 + b * cz + c * g.z2
    end

    -- 画面上の線分に直す。カメラの後ろの部分は落とす
    local segs = {}
    local px, py, pz = at(0)
    for i = 1, arrowSegments do
        local qx, qy, qz = at(i / arrowSegments)
        local ax, ay, bx, by = projectSegment(px, py, pz, qx, qy, qz)
        if ax ~= nil then segs[#segs + 1] = { ax = ax, ay = ay, bx = bx, by = by } end
        px, py, pz = qx, qy, qz
    end
    if #segs == 0 then return result end
    local hasTail = depth(g.x1, g.y1, g.z1) >= nearDepth
    local hasHead = depth(g.x2, g.y2, g.z2) >= nearDepth

    -- 先端の三角の分だけ線を先端側から削る。矢印が画面上で短いときは三角を小さくする
    local function segLen(s)
        local dx, dy = s.bx - s.ax, s.by - s.ay
        return math.sqrt(dx * dx + dy * dy)
    end
    local total = 0
    for _, s in ipairs(segs) do total = total + segLen(s) end
    local tipX, tipY = segs[#segs].bx, segs[#segs].by
    local baseX, baseY = tipX, tipY
    local headLen = 0
    if hasHead then
        headLen = math.min(22, total * 0.6)
        local rest = headLen
        while #segs > 0 do
            local s = segs[#segs]
            local l = segLen(s)
            if l > rest then
                local t = (l - rest) / l
                s.bx, s.by = s.ax + (s.bx - s.ax) * t, s.ay + (s.by - s.ay) * t
                baseX, baseY = s.bx, s.by
                break
            end
            rest = rest - l
            baseX, baseY = s.ax, s.ay
            table.remove(segs)
        end
    end

    result.line = {}
    for i, s in ipairs(segs) do result.line[i] = s end
    if hasHead then
        result.line[#result.line + 1] = { ax = baseX, ay = baseY, bx = tipX, by = tipY }
    end

    -- 線。縁取りを先に全部描いてから色を重ね、つなぎ目は丸で埋める
    for pass = 1, 2 do
        local col, width = outline, 8
        if pass == 2 then col, width = color, 5 end
        for i, s in ipairs(segs) do
            imgui.AddLine(s.ax, s.ay, s.bx, s.by, col, width)
            if i > 1 then imgui.AddCircleFilled(s.ax, s.ay, width * 0.5, col, 8) end
        end
    end

    if hasTail then
        local x, y = segs[1] and segs[1].ax or baseX, segs[1] and segs[1].ay or baseY
        imgui.AddCircleFilled(x, y, 6.5, outline, 16)
        imgui.AddCircleFilled(x, y, 5, color, 16)
        result.tail = { x = x, y = y }
    end

    local hx, hy = tipX - baseX, tipY - baseY
    local hl = math.sqrt(hx * hx + hy * hy)
    if hasHead and hl > 0.5 then
        local ux, uy = hx / hl, hy / hl
        local nx, ny = -uy, ux
        local headW = hl * 0.6
        local o = 2.5   -- 縁取りの太さ
        imgui.AddTriangleFilled(tipX + ux * o * 1.6, tipY + uy * o * 1.6,
            baseX - ux * o + nx * (headW + o), baseY - uy * o + ny * (headW + o),
            baseX - ux * o - nx * (headW + o), baseY - uy * o - ny * (headW + o), outline)
        imgui.AddTriangleFilled(tipX, tipY, baseX + nx * headW, baseY + ny * headW,
            baseX - nx * headW, baseY - ny * headW, color)
        result.head = { x = tipX - ux * hl * 0.6, y = tipY - uy * hl * 0.6, tipX = tipX, tipY = tipY }
    end

    -- 真ん中の点 (曲げるときに掴む所)。線の上に白い小さな点を出す
    if depth(g.xm, g.ym, g.zm) >= nearDepth then
        local sx, sy = worldToScreen(g.xm, g.ym, g.zm)
        sx, sy = resW * 0.5 + sx, resH * 0.5 + sy
        if showMid then
            imgui.AddCircleFilled(sx, sy, 4.5, outline, 12)
            imgui.AddCircleFilled(sx, sy, 3, 0xFFFFFFFF, 12)
        end
        result.mid = { x = sx, y = sy }
    end
    return result
end

-- 矢印は全部、画面全体を覆う透明なウィンドウ 1 枚に描く (入力は素通し)
local function drawArrows(view)
    local myName = view.name
    local visible = {}
    for _, a in ipairs(arrows) do
        if a.area == view.area and isShown(a, view) then visible[#visible + 1] = a end
    end
    if #visible == 0 and arrowDraft == nil then return end

    imgui.SetNextWindowPos(0, 0, "Always")
    imgui.SetNextWindowSize(resW, resH, "Always")
    imgui.PushStyleColor("WindowBg", 0, 0, 0, 0)
    imgui.PushStyleColor("Border", 0, 0, 0, 0)
    local handles = {}
    if imgui.Begin("PinShare##arrows", nil, markerWindowParams) then
        for _, a in ipairs(visible) do
            local g = arrowPosition(a)
            local movable = not a.locked and (a.owner == myName or options.moveOthers)
            local s = drawArrowShape(g, rgbToImU32(pinColor(a)), movable)
            if movable then
                -- 消すときは線のどこを指してもよい
                local circles = {}
                if s.tail then circles[#circles + 1] = { x = s.tail.x, y = s.tail.y, r = 8 } end
                if s.head then circles[#circles + 1] = { x = s.head.x, y = s.head.y, r = 10 } end
                table.insert(deleteTargets, { kind = "arrow", item = a, circles = circles, segs = s.line })
                -- 後に入れたものほど優先して掴まれるので、端を真ん中より優先する
                -- (画面上で短い矢印でも伸ばせるように)
                if s.mid then
                    handles[#handles + 1] = { kind = "arrow", item = a, point = 3, cx = s.mid.x, cy = s.mid.y, r = 8,
                                              tipX = s.mid.x, tipY = s.mid.y, groundY = g.ym }
                end
                if s.tail then
                    handles[#handles + 1] = { kind = "arrow", item = a, point = 1, cx = s.tail.x, cy = s.tail.y, r = 8,
                                              tipX = s.tail.x, tipY = s.tail.y, groundY = g.y1 }
                end
                if s.head then
                    handles[#handles + 1] = { kind = "arrow", item = a, point = 2, cx = s.head.x, cy = s.head.y, r = 10,
                                              tipX = s.head.tipX, tipY = s.head.tipY, groundY = g.y2 }
                end
            end
        end
        if arrowDraft ~= nil and arrowDraft.xm ~= nil then
            drawArrowShape(arrowDraft, rgbToImU32(myArrowColor() or myColor() or 0xFFFFFF), false)
        end
    end
    imgui.End()
    imgui.PopStyleColor(2)

    -- 掴む所の透明な窓は矢印のウィンドウの外で作る
    for _, h in ipairs(handles) do
        drawGrabArea(string.format("a%d_%d", h.item.id, h.point), h.cx, h.cy, h.r)
        table.insert(grabTargets, h)
    end
end

local function pinText(p, dist)
    local parts = { p.owner }
    if p.label ~= nil and p.label ~= "" then
        table.insert(parts, p.label)
    end
    if options.showDistance and dist ~= nil then
        table.insert(parts, string.format("%dm", math.floor(dist / 10)))
    end
    return table.concat(parts, " ")
end

local function drawPins()
    grabTargets = {}
    deleteTargets = {}
    -- クライアントが止まっていると in.txt が古いまま残るので表示しない
    if (#pins == 0 and #arrows == 0 and arrowDraft == nil) or not relayAlive() then return end
    local me = lib_characters.GetSelf()
    if me == 0 then return end
    local myPos = lib_characters.GetPlayerCoordinates(me)
    local view = filterView()
    local myName, area = view.name, view.area

    updateProjection()
    updateDragPosition(myPos)
    updateArrowDraft(myPos)
    -- 矢印を先に描いて、ピンを上に重ねる
    drawArrows(view)
    local hw = resW * 0.5 - 40
    local hh = resH * 0.5 - 40

    for _, p in ipairs(pins) do
        if p.area == area and isShown(p, view) then
            local px, py, pz = pinPosition(p)
            local dx, dy, dz = px - myPos.x, py - myPos.y, pz - myPos.z
            local dist = math.sqrt(dx * dx + dy * dy + dz * dz)
            local sx, sy, vis = worldToScreen(px, py, pz)
            local offscreen = (vis < 0) or (sx < -hw or sx > hw or sy < -hh or sy > hh)
            local color = rgbToImU32(pinColor(p))
            local text = pinText(p, dist)
            -- 掴める・クリックで消せるのは自分のピンだけ (設定で他の人のピンも)
            local movable = not p.locked and (p.owner == myName or options.moveOthers)

            if not offscreen then
                local tipX, tipY = resW * 0.5 + sx, resH * 0.5 + sy
                local cx, cy, r = drawPin(p, tipX, tipY, text, color)
                -- 掴めるのは画面内に見えているピンだけ
                if movable then
                    drawGrabArea(p.id, cx, cy, r)
                    table.insert(grabTargets, { kind = "pin", item = p, cx = cx, cy = cy, r = r,
                                                tipX = tipX, tipY = tipY, groundY = p.y })
                    table.insert(deleteTargets, { kind = "pin", item = p, circles = { { x = cx, y = cy, r = r } },
                                                  segs = { { ax = cx, ay = cy, bx = tipX, by = tipY } } })
                end
            elseif options.clampToEdge then
                -- 背面はベクトルを反転して端に出す
                if vis < 0 then sx, sy = -sx, -sy end
                local l = math.max(math.abs(sx) / hw, math.abs(sy) / hh)
                if l > 0 then
                    local len = math.sqrt(sx * sx + sy * sy)
                    local dirX, dirY = sx / len, sy / len
                    sx, sy = sx / l, sy / l
                    local cx, cy, r = drawPin(p, resW * 0.5 + sx, resH * 0.5 + sy, text, color, dirX, dirY)
                    -- 画面端に出ているピンも頭を指して消せる
                    if movable then
                        table.insert(deleteTargets, { kind = "pin", item = p, circles = { { x = cx, y = cy, r = r } } })
                    end
                end
            end
        end
    end
end

local function drawNotice()
    if notice == nil then return end
    if pso.get_tick_count() > noticeUntil then
        notice = nil
        return
    end
    local tw, th = imgui.CalcTextSize(notice)
    imgui.SetNextWindowPos(lib_helpers.GetResolutionWidth() * 0.5 - tw * 0.5 - 8,
        lib_helpers.GetResolutionHeight() * 0.2, "Always")
    if imgui.Begin("PinShare##notice", nil, { "NoTitleBar", "NoResize", "NoMove", "NoInputs",
            "NoSavedSettings", "AlwaysAutoResize" }) then
        imgui.TextColored(1.0, 0.8, 0.3, 1.0, notice)
    end
    imgui.End()
end

-- ---------------------------------------------------------------------------
-- 設定ウィンドウ
-- ---------------------------------------------------------------------------
local configWindowOpen = false
local capturingKey = nil   -- キー割り当て待ちの options のキー名

local function keyName(vk)
    if vk == 0 then return "(none)" end
    if vk >= 112 and vk <= 123 then return "F" .. (vk - 111) end
    if (vk >= 48 and vk <= 57) or (vk >= 65 and vk <= 90) then return string.char(vk) end
    if vk >= 96 and vk <= 105 then return "Num" .. (vk - 96) end
    local named = { [16] = "Shift", [17] = "Ctrl", [18] = "Alt", [19] = "Pause", [45] = "Insert", [46] = "Delete", [36] = "Home",
                    [35] = "End", [33] = "PageUp", [34] = "PageDown" }
    return named[vk] or string.format("VK %d", vk)
end

local function keyRow(label, optionKey)
    imgui.Text(string.format("%-16s %s", label, keyName(options[optionKey])))
    imgui.SameLine(260)
    imgui.PushID(optionKey)
    if capturingKey == optionKey then
        imgui.TextColored(1.0, 0.8, 0.2, 1.0, "Press a key...")
        imgui.SameLine()
        if imgui.Button("Cancel") then capturingKey = nil end
    else
        if imgui.Button("Set") then
            if padCapture ~= nil then setPadCapture(nil) end
            capturingKey = optionKey
        end
        imgui.SameLine()
        if imgui.Button("Unbind") then
            options[optionKey] = 0
            SaveOptions()
        end
    end
    imgui.PopID()
end

local function padBindingName(optionKey)
    local main, mod = options[optionKey], options[optionKey .. "Mod"]
    if main == 0 then return "(none)" end
    if mod == 0 then return padButtonName(main) end
    return padButtonName(mod) .. " + " .. padButtonName(main)
end

local function padRow(label, optionKey)
    imgui.Text(string.format("%-16s %s", label, padBindingName(optionKey)))
    imgui.SameLine(260)
    imgui.PushID(optionKey)
    if padCapture ~= nil and padCapture.key == optionKey then
        imgui.TextColored(1.0, 0.8, 0.2, 1.0, "Press...")
        imgui.SameLine()
        if imgui.Button("Cancel") then setPadCapture(nil) end
    else
        if imgui.Button("Set") then
            capturingKey = nil
            setPadCapture({ key = optionKey, order = {}, seen = 0 })
        end
        imgui.SameLine()
        if imgui.Button("Unbind") then
            options[optionKey] = 0
            options[optionKey .. "Mod"] = 0
            SaveOptions()
        end
    end
    imgui.PopID()
end

local function drawInputSection()
    keyRow("Pin in front:", "frontKey")
    imgui.PushItemWidth(160)
    local changed, value = imgui.InputInt("Front pin distance (10 = 1m)", options.frontDist, 10)
    if changed then
        options.frontDist = clamp(value, 10, 3000)
        SaveOptions()
    end
    imgui.PopItemWidth()

    imgui.Separator()
    imgui.Text("Controller")
    if inputLib == nil then
        imgui.TextColored(1.0, 0.6, 0.4, 1.0, "Not available: pinshare-input.dll is missing (update Rappy Runs Client)")
        imgui.TextColored(0.7, 0.7, 0.7, 1.0, "Without it, bound keys also trigger the game's own functions")
        return
    end
    -- bit 0 = キーボード、bit 1 = コントローラー、bit 2 = 準備完了 (それまでの 0 は失敗ではない)
    local status = inputLib.pinshare_input_status()
    if bit.band(status, 4) == 0 then
        imgui.TextColored(0.7, 0.7, 0.7, 1.0, "Starting...")
    else
        if bit.band(status, 2) == 0 then
            imgui.TextColored(1.0, 0.6, 0.4, 1.0, "Not available: the game's controller input could not be hooked")
        end
        if bit.band(status, 1) == 0 then
            imgui.TextColored(1.0, 0.6, 0.4, 1.0, "Bound keys may also trigger the game's own functions (keyboard hook failed)")
        end
    end
    for _, key in ipairs(padActionKeys) do
        padRow(padActionLabels[key], key)
    end
    imgui.TextColored(0.7, 0.7, 0.7, 1.0, "Set, then press one button, or hold a button and press another for a combo")
    imgui.TextColored(0.7, 0.7, 0.7, 1.0, "Bound buttons are held back from the game (for a combo, only the second button)")
end

-- 色の設定 1 行分。choices の中から選び、2 (Custom) なら R/G/B か色見本で選ぶ。
-- current は今の色の見本に出す色 (nil なら出さない)
local function colorSetting(label, id, modeKey, customKey, choices, current)
    local changed, value
    imgui.PushID(id)
    imgui.Text(label)
    for _, choice in ipairs(choices) do
        imgui.SameLine()
        if imgui.RadioButton(choice[2], options[modeKey] == choice[1]) then
            if options[modeKey] ~= choice[1] then
                options[modeKey] = choice[1]
                SaveOptions()
            end
        end
    end
    if current ~= nil then
        imgui.SameLine()
        local r, g, b = rgbToFloats(current)
        imgui.ColorButton(r, g, b, 1.0)
    end
    if options[modeKey] == 2 then
        -- R / G / B を個別に動かすか、下の色見本を押して選ぶ
        local custom = options[customKey]
        local rgb = { math.floor(custom / 65536) % 256, math.floor(custom / 256) % 256, custom % 256 }
        local rgbChanged = false
        imgui.PushItemWidth(60)
        for i, fmt in ipairs({ "R:%3.0f", "G:%3.0f", "B:%3.0f" }) do
            if i > 1 then imgui.SameLine() end
            changed, value = imgui.DragInt("##color" .. i, rgb[i], 1.0, 0, 255, fmt)
            if changed then
                rgb[i] = clamp(value, 0, 255)
                rgbChanged = true
            end
        end
        imgui.PopItemWidth()
        local swatches = {}
        for _, c in ipairs(slotColors) do swatches[#swatches + 1] = c end
        for _, c in ipairs(palette) do swatches[#swatches + 1] = c end
        for i, c in ipairs(swatches) do
            if i > 1 then imgui.SameLine() end
            local r, g, b = rgbToFloats(c)
            imgui.PushStyleColor("Button", r, g, b, 1)
            imgui.PushStyleColor("ButtonHovered", r, g, b, 0.8)
            imgui.PushStyleColor("ButtonActive", r, g, b, 0.6)
            if imgui.Button("  ##swatch" .. i) then
                rgb = { math.floor(c / 65536) % 256, math.floor(c / 256) % 256, c % 256 }
                rgbChanged = true
            end
            imgui.PopStyleColor(3)
        end
        if rgbChanged then
            options[customKey] = rgb[1] * 65536 + rgb[2] * 256 + rgb[3]
            SaveOptions()
        end
    end
    imgui.PopID()
end

local function drawConfig()
    local status
    imgui.SetNextWindowSize(480, 520, "FirstUseEver")
    status, configWindowOpen = imgui.Begin("Pin Share - Config", configWindowOpen)

    -- 接続状態
    if not relayAlive() then
        imgui.TextColored(1.0, 0.4, 0.4, 1.0, "Relay: not running")
        imgui.Text("Start Rappy Runs Client and turn on Pin Share in Settings")
    else
        if relay.status == "connected" then
            imgui.TextColored(0.4, 1.0, 0.4, 1.0, "Server: connected")
        elseif relay.status == "local" then
            -- 合言葉なしでピンセットだけ表示している
            imgui.TextColored(1.0, 0.8, 0.3, 1.0, "Server: not connected (no passphrase) - pin set only")
        elseif relay.status == "error" then
            imgui.TextColored(1.0, 0.4, 0.4, 1.0, "Server: " .. relay.message)
        else
            imgui.TextColored(1.0, 0.8, 0.3, 1.0, "Server: connecting... " .. relay.message)
        end
        imgui.Text("Channel: " .. relay.channel)
        imgui.Text("Members: " .. table.concat(relay.members, ", "))
        if relay.pinSet ~= nil then
            imgui.Text("Pin set: " .. relay.pinSet .. " (chosen on the site; locked)")
        end
    end

    imgui.Separator()
    local changed, value = imgui.Checkbox("Enable", options.enable)
    if changed then
        options.enable = value
        SaveOptions()
    end

    imgui.Separator()
    keyRow("Pin at cursor:", "cursorKey")
    keyRow("Pin at feet:", "feetKey")
    keyRow("Clear mine:", "clearKey")
    keyRow("Arrow (hold):", "arrowKey")
    keyRow("Delete (hold):", "deleteKey")
    keyRow("Cycle filter:", "filterKey")
    imgui.TextColored(0.7, 0.7, 0.7, 1.0, "Hold the arrow key and drag with the pin mouse button to draw an arrow")
    imgui.TextColored(0.7, 0.7, 0.7, 1.0, "Hold the delete key and click a pin or an arrow with the pin mouse button to delete it")
    drawInputSection()
    imgui.Separator()
    imgui.Text("Mouse pin at cursor:")
    for _, choice in ipairs({ { 1, "Right click" }, { 2, "Middle click" }, { 0, "Off" } }) do
        imgui.SameLine()
        if imgui.RadioButton(choice[2], options.mouseButton == choice[1]) then
            if options.mouseButton ~= choice[1] then
                options.mouseButton = choice[1]
                lastMouseClick = nil
                SaveOptions()
            end
        end
    end
    -- 診断: クリックが届いているか・どう処理されたか
    if options.mouseButton ~= 0 then
        if lastMouseClick == nil then
            imgui.TextColored(0.7, 0.7, 0.7, 1.0, "Last click: none detected")
        else
            local ago = math.floor((pso.get_tick_count() - lastMouseClick.tick) / 1000)
            imgui.TextColored(0.7, 0.7, 0.7, 1.0,
                string.format("Last click: %s (%ds ago)", lastMouseClick.result, ago))
        end
    end

    imgui.Separator()
    imgui.PushItemWidth(160)
    changed, value = imgui.InputInt("Pin lifetime (sec, 0 = forever)", options.ttl)
    if changed then
        options.ttl = clamp(value, 0, 3600)
        SaveOptions()
    end
    changed, value = imgui.InputInt("Max pins (per player, 0 = no limit)", options.maxPins)
    if changed then
        options.maxPins = math.max(value, 0)
        SaveOptions()
    end
    changed, value = imgui.InputText("Label", options.label, 24)
    if changed then
        options.label = value
        SaveOptions()
    end
    changed, value = imgui.InputInt("Max cursor distance", options.maxCursorDist, 100)
    if changed then
        options.maxCursorDist = clamp(value, 0, 20000)
        SaveOptions()
    end
    changed, value = imgui.SliderFloat("Font scale", options.fontScale, 0.5, 3.0)
    if changed then
        options.fontScale = value
        SaveOptions()
    end
    imgui.PopItemWidth()

    colorSetting("My pin color:", "pincolor", "colorMode", "customColor",
        { { 1, "Room entry order" }, { 2, "Custom" } }, myColor())
    colorSetting("My arrow color:", "arrowcolor", "arrowColorMode", "arrowCustomColor",
        { { 0, "Same as pins" }, { 1, "Room entry order" }, { 2, "Custom" } }, myArrowColor() or myColor())

    imgui.Text("Show pins:")
    for _, choice in ipairs(filterChoices) do
        imgui.SameLine()
        if imgui.RadioButton(choice[2] .. "##filter", options.pinFilter == choice[1]) then
            if options.pinFilter ~= choice[1] then
                options.pinFilter = choice[1]
                SaveOptions()
            end
        end
    end

    imgui.Text("Pin numbers:")
    for _, choice in ipairs({ { 1, "Per player" }, { 3, "Per room" }, { 2, "All pins" }, { 0, "Off" } }) do
        imgui.SameLine()
        if imgui.RadioButton(choice[2] .. "##order", options.orderMode == choice[1]) then
            if options.orderMode ~= choice[1] then
                options.orderMode = choice[1]
                SaveOptions()
            end
        end
    end
    imgui.PushItemWidth(160)
    changed, value = imgui.InputInt("Max pin number (0 = no limit)", options.orderMax)
    if changed then
        options.orderMax = clamp(value, 0, 2147483647)
        SaveOptions()
    end
    imgui.PopItemWidth()
    changed, value = imgui.Checkbox("Allow dragging and click-deleting other players' pins and arrows", options.moveOthers)
    if changed then
        options.moveOthers = value
        SaveOptions()
    end
    changed, value = imgui.Checkbox("Show distance", options.showDistance)
    if changed then
        options.showDistance = value
        SaveOptions()
    end
    changed, value = imgui.Checkbox("Show off-screen pins at screen edge", options.clampToEdge)
    if changed then
        options.clampToEdge = value
        SaveOptions()
    end
    changed, value = imgui.Checkbox("Hide pins when menu is open", options.hideWhenMenu)
    if changed then
        options.hideWhenMenu = value
        SaveOptions()
    end

    -- ピン一覧。表示の絞り込みに合うものだけ出す
    imgui.Separator()
    local view = filterView()
    if view.room1 ~= nil then
        imgui.TextColored(0.7, 0.7, 0.7, 1.0, string.format("You are in: %s room %d", areaName(view.area), view.room1))
    end
    local shownPins, shownArrows = {}, {}
    for _, p in ipairs(pins) do
        if isShown(p, view) then shownPins[#shownPins + 1] = p end
    end
    for _, a in ipairs(arrows) do
        if isShown(a, view) then shownArrows[#shownArrows + 1] = a end
    end
    -- どこに置かれたものか。今いる部屋なら "this room"、同じエリアの別の部屋なら "room N"
    local function whereText(item)
        if inMyRoom(item, view) then return "this room" end
        local room = item.room and string.format("room %d", item.room) or nil
        if item.area == view.area then return room or "here" end
        return room and (areaName(item.area) .. " " .. room) or areaName(item.area)
    end
    local function listTitle(name, shown, all)
        if shown == all then return string.format("%s (%d)", name, all) end
        return string.format("%s (%d of %d shown)", name, shown, all)
    end
    imgui.Text(listTitle("Pins", #shownPins, #pins))
    for _, p in ipairs(shownPins) do
        imgui.PushID("pin" .. p.id)
        if p.locked then
            -- ピンセットのピンはサイトで選んだもの。ここでは消せない
            imgui.TextDisabled("Locked")
        elseif imgui.Button("Delete") then
            sendCommand({ "remove", p.id })
        end
        imgui.SameLine()
        local r, g, b = rgbToFloats(pinColor(p))
        local where = whereText(p)
        local life = (p.remaining >= 0) and string.format("%ds", p.remaining) or "-"
        local order = pinOrder(p)
        local num = order and string.format("#%d  ", order) or ""
        imgui.TextColored(r, g, b, 1.0, string.format("%s%s  %s  %s  %s", num, p.owner, p.label, where, life))
        imgui.PopID()
    end
    imgui.Text(listTitle("Arrows", #shownArrows, #arrows))
    for _, a in ipairs(shownArrows) do
        imgui.PushID("arrow" .. a.id)
        if a.locked then
            imgui.TextDisabled("Locked")
        elseif imgui.Button("Delete") then
            sendCommand({ "arrow_remove", a.id })
        end
        imgui.SameLine()
        local r, g, b = rgbToFloats(pinColor(a))
        local where = whereText(a)
        local life = (a.remaining >= 0) and string.format("%ds", a.remaining) or "-"
        local dx, dz = a.x2 - a.x1, a.z2 - a.z1
        local length = math.floor(math.sqrt(dx * dx + dz * dz) / 10)
        imgui.TextColored(r, g, b, 1.0, string.format("%s  %dm  %s  %s", a.owner, length, where, life))
        imgui.PopID()
    end
    -- ピンと矢印をまとめて消す
    if imgui.Button("Clear mine") then clearMyPins() end
    imgui.SameLine()
    if imgui.Button("Clear all") then
        if canPlacePin() then sendCommand({ "clear_all" }) end
    end

    imgui.End()
end

-- ---------------------------------------------------------------------------
-- present
-- ---------------------------------------------------------------------------
local pollInput  -- 下の「キー入力」で定義 (cycleFilter などを使うため)

local function present()
    local now = pso.get_tick_count()
    -- 割り当て待ちのままウィンドウを閉じたら取り消す (放置するとキー操作もコントローラーも止まったままになる)
    if padCapture ~= nil and not configWindowOpen then setPadCapture(nil) end
    updatePadCapture()
    syncInputBindings()
    pollInput()
    if now - lastReadTick >= readInterval then
        lastReadTick = now
        readInbox()
        updateSlots()
        syncName()
        syncColor()
    end

    if options.enable and not lib_menu.IsMenuUnavailable() then
        if not (options.hideWhenMenu and lib_menu.IsMenuOpen()) then
            drawPins()
        else
            grabTargets = {}
            deleteTargets = {}
        end
        updateDrag()
        finishArrow()

        -- マウスでカーソル位置にピン (arrowKey を押しながらなら矢印を引き始める)。
        -- deleteKey を押しながらピン・矢印を指していればそれを消す。何も指していなければ
        -- 普段どおりに扱うので、deleteKey と arrowKey が同じキーでも地面からは矢印を引ける。
        -- ボタンなどの操作部品の上でのクリックだけ除く。
        -- ウィンドウ単位で除くと、画面を大きく覆う他のアドオンのウィンドウがある環境で
        -- どこをクリックしても反応しなくなる。
        if options.mouseButton ~= 0 and imgui.IsMouseClicked(options.mouseButton) and arrowDraft == nil then
            local result
            if not pso.is_pso_focused() then
                result = "ignored: PSO window is not focused"
            elseif imgui.IsAnyItemHovered() then
                result = "ignored: cursor is over a button"
            else
                result = isKeyDown(options.deleteKey) and deleteAtCursor() or nil
                if result == nil then
                    if isKeyDown(options.arrowKey) then
                        result = startArrowAtCursor(options.mouseButton)
                    else
                        result = placePinAtCursor()
                    end
                end
            end
            lastMouseClick = { tick = now, result = result }
        end
    else
        arrowDraft = nil
    end

    drawNotice()

    if configWindowOpen then
        drawConfig()
    end
end

-- ---------------------------------------------------------------------------
-- キー入力
-- ---------------------------------------------------------------------------
-- 表示の絞り込みを 全員 -> 同じ部屋 -> 自分 -> 全員 … の順に切り替える
local function cycleFilter()
    local index = 1
    for i, choice in ipairs(filterChoices) do
        if choice[1] == options.pinFilter then index = i % #filterChoices + 1 end
    end
    local choice = filterChoices[index]
    options.pinFilter = choice[1]
    SaveOptions()
    showNotice("Pin Share: show pins - " .. choice[2])
end

local function keyPressed(key)
    -- 設定ウィンドウでキー割り当て待ちなら、押されたキーを割り当てる
    if capturingKey ~= nil then
        options[capturingKey] = key
        capturingKey = nil
        SaveOptions()
        return
    end

    if not options.enable or key == 0 or padCapture ~= nil then return end

    if key == options.cursorKey then
        placePinAtCursor()
    elseif key == options.feetKey then
        placePinAtFeet()
    elseif key == options.clearKey then
        clearMyPins()
    elseif key == options.filterKey then
        cycleFilter()
    elseif key == options.frontKey then
        placePinInFront()
    end
end

-- コントローラーの割り当ての押下を DLL から受け取る。毎フレーム呼ぶ (DLL の隠す処理を生かしておく合図を兼ねる)
pollInput = function()
    if inputLib == nil then return end
    local fired = inputLib.pinshare_input_poll()
    if fired == 0 or not options.enable then return end
    local run = { padFeet = placePinAtFeet, padClear = clearMyPins, padFilter = cycleFilter, padFront = placePinInFront }
    for i, key in ipairs(padActionKeys) do
        if bit.band(fired, bit.lshift(1, i - 1)) ~= 0 then run[key]() end
    end
end

local function init()
    core_mainmenu.add_button("Pin Share", function()
        configWindowOpen = not configWindowOpen
    end)

    return {
        name = "Pin Share",
        version = "1.0.0",
        author = "g23tl",
        description = "Place pins on the ground and share them with your party via a relay server.",
        present = present,
        key_pressed = keyPressed,
        toggleable = true,
    }
end

return {
    __addon = {
        init = init,
    },
}
