@echo off
setlocal
cd /d "%~dp0"
echo === claude-task-bar : local git setup ===
if exist ".git" (
  echo Removing partial .git created by the sandbox ...
  rmdir /s /q ".git"
)
git init
git add README.md .gitignore
git commit -m "chore: initial scaffold (README, .gitignore, design + mascot spec)"
git branch -M main
echo.
echo Local repo is ready on branch 'main'.
echo.
echo To connect GitHub (after you create an EMPTY repo named claude-task-bar on GitHub):
echo    git remote add origin https://github.com/YOUR_USERNAME/claude-task-bar.git
echo    git push -u origin main
echo.
pause
endlocal
