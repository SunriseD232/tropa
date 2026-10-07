#!/bin/bash
# usage: assemble.sh <Name> <title> <navLabel> <height> <bodyfile>
name=$1; title=$2; navl=$3; h=$4; body=$5
out=project/$name.dc.html
nav=$(sed -e 's/class="nav on" aria-current="page"/class="nav"/' nav.part | sed -e "s/<button class=\"nav\">\(<svg[^>]*>.*<\/svg>\)$navl<\/button>/<button class=\"nav on\" aria-current=\"page\">\1$navl<\/button>/")
{
cat <<H
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<title>$title</title>
<script src="./support.js"></script>
</head>
<body>
<x-dc>
H
sed -e "s/min-height:820px/min-height:${h}px/" helmet.part
echo '<div class="app">'
echo "$nav"
cat "$body"
echo '</div>'
echo '</x-dc>'
cat <<F
<script type="text/x-dc" data-dc-script data-props='{"\$preview":{"width":1280,"height":$h}}'>
class Component extends DCLogic {
  renderVals() {
    return {};
  }
}
</script>
</body>
</html>
F
} > "$out"
grep -c 'nav on' "$out"
