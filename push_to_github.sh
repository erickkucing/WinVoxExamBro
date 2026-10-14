#!/usr/bin/env bash
# ==============================================================================
# WinVoxExamBro - Push to GitHub Helper
# ==============================================================================

set -Eeuo pipefail

GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
RED='\033[0;31m'
BOLD='\033[1m'
NC='\033[0m'

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$PROJECT_DIR"

export PATH="$PATH:/root/.dotnet"

echo -e "${BLUE}==================================================================${NC}"
echo -e "${BOLD}${CYAN}            WINVOXEXAMBRO - PUSH TO GITHUB HELPER               ${NC}"
echo -e "${BLUE}==================================================================${NC}"

# 1. Pastikan Git & Cabang
echo -e "${YELLOW}[1/5] Memeriksa Cabang Git Aktif...${NC}"
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "main")
echo -e "  ${GREEN}✓ Cabang aktif: ${CYAN}${CURRENT_BRANCH}${NC}"

# 2. Remote Origin
echo -e "\n${YELLOW}[2/5] Memeriksa Remote Origin GitHub...${NC}"
REMOTE_URL=$(git remote get-url origin 2>/dev/null || echo "")
if [ -z "$REMOTE_URL" ]; then
    DEFAULT_REMOTE="git@github.com:erickkucing/WinVoxExamBro.git"
    git remote add origin "$DEFAULT_REMOTE"
    REMOTE_URL="$DEFAULT_REMOTE"
fi
echo -e "  ${GREEN}✓ Remote origin: ${CYAN}${REMOTE_URL}${NC}"

# 3. Jalankan Pengujian Otomatis
echo -e "\n${YELLOW}[3/5] Menjalankan Unit Test (.NET 8)...${NC}"
if dotnet test tests/ExamBro.Tests/ExamBro.Tests.csproj -c Release --verbosity quiet; then
    echo -e "  ${GREEN}✓ Seluruh unit test lolos tanpa kesalahan!${NC}"
else
    echo -e "  ${RED}✗ Pengujian gagal! Perbaiki error sebelum push.${NC}"
    exit 1
fi

# 4. Input Pesan Commit & Staging
echo -e "\n${YELLOW}[4/5] Menyiapkan Commit...${NC}"
COMMIT_MSG="${1:-}"
if [ -z "$COMMIT_MSG" ]; then
    DEFAULT_MSG="Update WinVoxExamBro - $(date '+%Y-%m-%d %H:%M:%S')"
    read -r -p "Masukkan pesan commit [Tekan Enter untuk: '${DEFAULT_MSG}']: " USER_INPUT_MSG || true
    COMMIT_MSG="${USER_INPUT_MSG:-$DEFAULT_MSG}"
fi

git add .
if git diff-index --quiet HEAD -- 2>/dev/null; then
    echo -e "  ${YELLOW}Tidak ada perubahan baru untuk di-commit.${NC}"
else
    git commit -m "$COMMIT_MSG"
    echo -e "  ${GREEN}✓ Commit baru dibuat: ${CYAN}${COMMIT_MSG}${NC}"
fi

# 5. Push ke GitHub
echo -e "\n${YELLOW}[5/5] Mengunggah (Push) ke GitHub (${CURRENT_BRANCH})...${NC}"
if git push -u origin "$CURRENT_BRANCH"; then
    echo -e "\n${GREEN}==================================================================${NC}"
    echo -e "${BOLD}${GREEN}           KODE BERHASIL DI-PUSH KE GITHUB!                       ${NC}"
    echo -e "${GREEN}==================================================================${NC}"
    echo -e "  - Repositori : ${CYAN}${REMOTE_URL}${NC}"
    echo -e "  - Cabang     : ${CYAN}${CURRENT_BRANCH}${NC}"
    echo -e "  - Pipeline   : GitHub Actions CI/CD akan otomatis berjalan pada:"
    echo -e "                 https://github.com/erickkucing/WinVoxExamBro/actions"
    echo -e "=================================================================="
else
    echo -e "\n${YELLOW}Peringatan: Push gagal. Mencoba rekonsiliasi dengan rebase...${NC}"
    if git pull --rebase origin "$CURRENT_BRANCH" && git push -u origin "$CURRENT_BRANCH"; then
        echo -e "\n${GREEN}✓ Push berhasil setelah rekonsiliasi!${NC}"
    else
        echo -e "\n${RED}[GAGAL PUSH] Pastikan repository di GitHub sudah dibuat di https://github.com/new${NC}"
        exit 1
    fi
fi
