// Hand-curated inputs for update.mjs: what the open lists miss, what they get wrong for this product, and what no
// blocking list may contain. The reasons are summarised in data/starter-lists/README.md.

/** Lists whose domains (and the parents of those domains) no sourced blocking list may contain. */
export const ALLOW_LISTS = ["educational", "school-tools"];

/**
 * Never in a sourced blocking list, nor a parent of these: platforms, CDNs and hosting services whose pages or frames
 * appear on ordinary sites, so blocking them would break general browsing. A specific subdomain below them (one
 * site's own host) may still be listed.
 */
export const NEVER_BLOCK = [
  // Google, Microsoft, Apple, Amazon
  "google.com", "google.co.il", "googleusercontent.com", "googleapis.com", "gstatic.com", "ggpht.com", "goo.gl",
  "youtube.com", "youtu.be", "ytimg.com", "blogspot.com", "blogger.com", "recaptcha.net",
  "microsoft.com", "live.com", "office.com", "office.net", "microsoftonline.com", "windows.net", "azureedge.net",
  "msn.com", "bing.com", "skype.com", "cloud.microsoft", "static.microsoft", "linkedin.com", "github.com", "github.io",
  "githubusercontent.com", "apple.com", "icloud.com", "mzstatic.com", "amazon.com", "amazonaws.com", "cloudfront.net",
  "media-amazon.com",
  // Social and messaging platforms (blocked, when wanted, by the "social" list)
  "facebook.com", "fbcdn.net", "instagram.com", "whatsapp.com", "whatsapp.net", "messenger.com", "threads.net",
  "x.com", "twitter.com", "twimg.com", "t.co", "reddit.com", "redd.it", "redditmedia.com", "redditstatic.com",
  "tumblr.com", "imgur.com", "pinterest.com", "pinimg.com", "discord.com", "discord.gg", "discordapp.com",
  "discordapp.net", "telegram.org", "telegram.me", "t.me", "tiktok.com", "snapchat.com", "vk.com", "ok.ru",
  "twitch.tv", "kick.com", "bsky.app", "patreon.com", "deviantart.com", "pixiv.net",
  // Reference, code and hosting
  "wikipedia.org", "wikimedia.org", "archive.org", "gitlab.com", "bitbucket.org", "wordpress.com", "wp.com",
  "medium.com", "substack.com", "wix.com", "wixsite.com", "squarespace.com", "weebly.com", "shopify.com",
  "myshopify.com", "dropbox.com", "mega.nz", "mediafire.com", "vercel.app", "netlify.app", "pages.dev", "workers.dev",
  "herokuapp.com", "firebaseapp.com", "web.app", "appspot.com", "azurewebsites.net",
  // CDNs
  "cloudflare.com", "cloudflare.net", "akamai.net", "akamaihd.net", "akamaized.net", "edgekey.net", "edgesuite.net",
  "fastly.net", "fastly.com", "jsdelivr.net", "unpkg.com", "cdnjs.com", "bunny.net", "b-cdn.net",
  // Payments and everyday services
  "paypal.com", "stripe.com", "netflix.com", "spotify.com", "zoom.us", "zoom.com", "yahoo.com", "yandex.ru",
  "duckduckgo.com",
];

/**
 * In every sourced list, hosts below these are left out: CDN and cloud-storage hosts are another site's assets or DNS
 * aliases, not pages, so listing them only adds noise.
 */
export const DROP_UNDER = [
  "amazonaws.com", "azureedge.net", "b-cdn.net", "cdn.cloudflare.net", "cdn77.org", "cloudfront.net", "netdna-ssl.com",
  "trafficmanager.net",
];

export const ADULT = {
  remove: [
    // General sites, stores, media and services
    "articulo.mercadolibre.com.co", "blogs.icrc.org", "booth.pm", "chomikuj.pl", "ekstrabladet.dk", "fark.com",
    "fc2.com", "hmv.co.jp", "kinopoisk.ru", "mafab.hu", "marieclaire.it", "more.com", "musvozimbabwenews.com",
    "my.mail.ru", "quoka.de", "skiselectsports.com", "suruga-ya.jp", "virginradio.fr", "yurikamome.co.jp",
    "dmm.com", // DMM's general store, games and English lessons (its adult store is dmm.co.jp, which stays)
    "fairfaxcryobank.com", "seattlespermbank.com", // sperm banks
    // General file, image and video hosts, manga and anime sites
    "anime-loads.org", "animeaddicts.hu", "imageban.ru", "niadd.com", "pixhost.to", "streamruby.com",
    "streamtape.com", "streamtape.to",
    // Piracy (torrents, games, TV and sports streaming), not adult
    "1337x.to", "1377x.to", "gamepcfull.com", "gimytv.tw", "igg-games.com", "katcr.to", "kickasstorrents.to",
    "rargb.to", "rutor.info", "rutracker.net", "sportbud.org", "sportcategory.org", "sportlemon.net",
    "thepiratebay0.org", "torrentdownloads.pro", "torrentfunk.com",
    // Belong to other lists: a proxy service and ad networks
    "hidemyass.com", "hideproxy.me", "exoclick.com", "juicyads.com", "popcash.net",
  ],
  add: [
    "chub.ai", // NSFW AI role-play chat (the sources already list crushon.ai, spicychat.ai and candy.ai)
    "janitorai.com", // NSFW AI role-play chat
  ],
};

export const GAMBLING = {
  remove: [
    "cricketweb.net", // cricket news
    "livescore.in", // live sports scores
    "pagcor.ph", // a government regulator
    "playtech.com", // a software supplier's corporate site
    "playtika.com", // a game company's corporate site
    "sportradar.com", // a sports-data company
    "taptap.io", // a game store
  ],
  add: [
    // Israel: the legal lottery and sports betting, and lottery-ticket services
    "winner.co.il", "pais.co.il", "lottosheli.co.il", "lottoishi.co.il", "lottoplus.co.il", "lottonline.co.il",
    // Large international sites the sources miss
    "1win.com", "1win.pro", "ggpoker.com", "hardrock.bet", "kalshi.com", "mostbet.com", "pin-up.casino",
    "pokerstars.com", "roobet.com", "shuffle.com", "vavada.com", "wowvegas.com",
  ],
};

export const PROXIES = {
  /** Also left out here: VPN apps' API and server endpoints (not pages). */
  dropUnder: [
    "appspot.com", "cloudfunctions.net", "firebasedatabase.app", "firebaseio.com", "googleapis.com", "prcdn.net",
    "webpubsub.azure.com",
  ],
  remove: [
    // Corporate VPN, zero-trust and remote-access products (school and work networks), and other non-bypass sites
    "checkpoint.com", "ewon.biz", "fortinet.com", "fortinet.net", "fortiwebcloud.net", "gpcloudservice.com",
    "mus.cisco.com", "msc.mcafee.com", "openvpn.com", "openvpn.net", "sparklabs.com", "stunnel.org", "tailscale.com",
    "talk2m.com", "twingate.com", "vpn.azure.com", "vpn.nvidia.com", "wireguard.com", "zpath.net", "zscalertwo.net",
    "spaceship.com", "spaceship-cdn.com", "wellpoint.com",
    // Browser vendors' own privacy relays (not pages)
    "fpn.firefox.com", "ipprotectionauth.goog", "testpilot.firefox.com", "testpilot.r53-2.services.mozilla.com",
    "tunnel.googlezip.net",
  ],
  add: [
    // Page-translation and cache proxies that show any site under their own host
    "translate.goog", // Google Translate serves every translated site as <site>.translate.goog
    "translate.googleusercontent.com", // Google Translate's older proxy host
    "webcache.googleusercontent.com", // Google's retired page cache
    "translatetheweb.com", // Microsoft Translator's web-page translation
    "translatoruser-int.com", // Microsoft Translator's proxy host for translated pages
    "turbopages.org", // Yandex Translate and Turbo pages (translated.turbopages.org)
    "z5h64q92x9.net", // Yandex Translate's older proxy host
    // Archive snapshots on demand (any page, as it looked)
    "archive.today", "archive.ph", "archive.is", "archive.li", "archive.vn", "archive.fo", "archive.md",
    // Remote browsers and well-known web proxies
    "browserling.com", "hyperbeam.com", "croxyproxy.com", "croxy.network", "croxy.org", "blockaway.net",
    "proxyium.com", "titaniumnetwork.org", "12ft.io",
  ],
};

export const ADS = {
  remove: [
    "s.id", // a URL shortener, not an ad network
  ],
  add: [
    // Google: search-ad and display-ad click-throughs, and display-ad frames
    "googleadservices.com", "doubleclick.net", "googlesyndication.com", "dartsearch.net",
    // Other display networks: ad frames and click-throughs
    "adnxs.com", "amazon-adsystem.com", "criteo.com", "criteo.net", "adsrvr.org",
    // Native "around the web" networks: sponsored click-throughs
    "paid.outbrain.com", "mgid.com", "revcontent.com", "adskeeper.com",
    // Popunder, push and redirect networks, many of them on adult and piracy sites
    "a-ads.com", "ad-maven.com", "adcash.com", "adnium.com", "adspyglass.com", "adsterra.com", "adxpansion.com",
    "bidvertiser.com", "clickadilla.com", "clickadu.com", "effectivegatecpm.com", "ero-advertising.com",
    "exdynsrv.com", "exoclick.com", "exosrv.com", "galaksion.com", "highperformanceformat.com", "hilltopads.com",
    "hilltopads.net", "juicyads.com", "magsrv.com", "monetag.com", "myadcash.com", "onclickads.net", "popads.net",
    "popcash.net", "popmyads.com", "profitablecpmratenetwork.com", "propellerads.com", "propellerclick.com",
    "realsrv.com", "richads.com", "trafficfactory.biz", "trafficjunky.com", "trafficjunky.net", "trafficshop.com",
    "tsyndicate.com", "yllix.com", "zeropark.com",
  ],
};
