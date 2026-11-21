#!/bin/bash
# DoH layers (spike A0.20). Usage: doh-layers.sh <layerName> <dohNames:true|false> <dohIp:true|false> <sni:true|false>
# Runs the same four probes for each layer; results go to evidence/e20-doh-<layer>.txt
source "$(dirname "$0")/lib.sh"
export MSYS_NO_PATHCONV=1
LAYER="$1"; DN="$2"; DI="$3"; SN="$4"
# DoH IPs resolved through the tunnel at test time (see e20-15-B-dohhost-ips.txt):
IPS="8.8.8.8,8.8.4.4,104.16.248.249,104.16.249.249,1.1.1.1,1.0.0.1"
OUT="$EVI/e20-doh-$LAYER.txt"
: > "$OUT"

timeout 60 adb $S shell am broadcast -W -n $DPC/.CmdReceiver --es op rules --ez dohNames "$DN" --ez dohIp "$DI" --ez sni "$SN" --es ips "$IPS" >/dev/null
sleep 2
logclear
probe doh_sni --es host dns.google --es name en.wikipedia.org --es path /resolve
sleep 9
probe doh_sni --es host dns.google --es ip 8.8.8.8 --es name en.wikipedia.org --es path /resolve
sleep 9
probe doh_sni --es host cloudflare-dns.com --es ip 104.16.248.249 --es name en.wikipedia.org --es path /dns-query
sleep 9
probe doh_sni --es host en.wikipedia.org --es ip 185.15.59.224 --es name example.org --es path /
sleep 9
timeout 15 adb $S logcat -d -s A020P:I A020:I | grep -E "A020P|RULES|sni blocked|doh ip refused|dns blocked" > "$OUT"
cat "$OUT"
