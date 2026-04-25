; RUN: @irie-mc --assemble @file | @irie-mc --disassemble

; CHECK: main:
main:
; CHECK:     LDA #\$00
    LDA #$00
; CHECK:     STA \$F0
    STA $F0
; CHECK: \.loop:
.loop:
; CHECK:     LDA \$F0
    LDA $F0
; CHECK:     BEQ \.done
    BEQ .done
; CHECK:     JSR helper
    JSR helper
; CHECK:     JMP \.loop
    JMP .loop
; CHECK: \.done:
.done:
; CHECK:     RTS
    RTS
